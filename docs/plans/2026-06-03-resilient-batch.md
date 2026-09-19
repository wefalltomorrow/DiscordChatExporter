# Resilient Batch (per-channel checkpoint, resume, retry) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make whole-server/multi-channel export resilient and resumable: each channel is checkpointed to the manifest the moment it completes, so re-running an interrupted export skips the channels already done; plus a one-click "Retry failed" for channels that errored.

**Architecture:** The parallel export loop already continues past a single channel's non-fatal failure. We add three things: (1) the manifest is written **per channel as it completes** (thread-safe), turning it into a live checkpoint that survives a crash; (2) on a new export, channels whose output file is already in the target dir's manifest are detected and — via a prompt — skipped (resume); (3) the failed channels are tracked so "Retry failed (N)" can re-run just them.

**The correctness invariant (the thing tests must protect):** *a channel is in the manifest **iff** its export completed.* Completed → entry written → skipped on resume. Empty channel → entry (count 0) → skipped (it's done). Failed → no entry → re-attempted. Crashed mid-export → no entry + partial file → re-exported (`File.Create` truncates the partial). So **resume already retries failures** — the explicit "Retry failed" button (Task 4) is pure convenience and is sequenced last.

**Tech Stack:** C#/.NET 10, `AsyncKeyedLock` (already a dependency, used in `ExportAssetDownloader`), Avalonia 12. Tests: xUnit + FluentAssertions in `DiscordChatExporter.Cli.Tests` (token-free).

**Design decisions (locked, per advisor):**
- **No new `UpsertEntryAsync`** — `ManifestWriter.WriteAsync` already reads-merges-by-file-writes (it IS an upsert). #3 only adds **thread-safety** (a `static` `AsyncKeyedLocker<string>` keyed by manifest path) so it's safe to call per-channel from the parallel loop.
- **No persisted run-descriptor.** The user's re-selection is the target set; the manifest is the completed set; a prompt resolves intent. (Auto-resume-on-relaunch belongs with the later watch/jobs work.)
- **Match by file name** (`Path.GetFileName(request.OutputFilePath)`), case-insensitive — date-range-safe (date range is in the filename). NOT channelId+format (manifest stores no After/Before → would collide full vs date-ranged exports). Known gap: a channel **renamed on Discord** between runs gets a new filename → re-exported, old file orphaned — rare, degrades safely, **not handled by design**.
- **Prompt only on actual overlap.** No overlap → no prompt, export all. All selected already done → "already up to date", export nothing (no silent no-op).
- Per-channel full-manifest rewrite is O(N²) IO — fine for hundreds of channels; **do not batch** (adds flush-timing + lost-work complexity for no real gain).

---

### Task 1: Make `ManifestWriter.WriteAsync` thread-safe

**Files:**
- Modify: `DiscordChatExporter.Core/Exporting/Manifest/ManifestWriter.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestWriterSpecs.cs` (add to existing)

- [ ] **Step 1: Write the failing concurrency test**

Add this test to the existing `ManifestWriterSpecs` class (it already has an `Entry(file, count)` helper and a temp `_dir`):

```csharp
    [Fact]
    public async Task Concurrent_writes_to_the_same_manifest_do_not_lose_entries()
    {
        // Fire many parallel writes, each adding a distinct file. Without serialization,
        // the read-merge-write race would clobber entries (last-writer-wins on the whole file).
        const int count = 50;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            async (i, ct) =>
                await ManifestWriter.WriteAsync(_dir, [Entry($"file{i}.json", i)], DateTimeOffset.UnixEpoch, ct)
        );

        var manifest = await ManifestReader.TryReadAsync(Path.Combine(_dir, ExportManifest.FileName));
        manifest.Should().NotBeNull();
        manifest!.Entries.Should().HaveCount(count);
    }
```

Add `using System.Linq;` and `using System.Threading.Tasks;` to the test file if not already present.

- [ ] **Step 2: Run it to verify it fails (or is flaky)**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Concurrent_writes"`
Expected: FAIL (fewer than 50 entries — lost updates from the unsynchronized read-merge-write). If it happens to pass by luck, that's acceptable; the lock in Step 3 makes it deterministic.

- [ ] **Step 3: Add the static lock**

In `ManifestWriter.cs`, add `using AsyncKeyedLock;` at the top. Add a static locker field at the top of the class:

```csharp
    // Serializes the read-merge-write of a given manifest file so per-channel checkpoint writes
    // from the parallel export loop don't clobber each other. Keyed by manifest path; MUST be static.
    private static readonly AsyncKeyedLocker<string> Locker = new();
```

In `WriteAsync`, immediately after `var manifestPath = Path.Combine(dirPath, ExportManifest.FileName);`, acquire the lock for the rest of the method:

```csharp
        var manifestPath = Path.Combine(dirPath, ExportManifest.FileName);

        using var _ = await Locker.LockAsync(manifestPath, cancellationToken);

        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        // ... rest of the method unchanged ...
```

(Mirror the exact `using var _ = await Locker.LockAsync(...)` idiom from `ExportAssetDownloader.cs:30`.)

- [ ] **Step 4: Run the full writer suite**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestWriterSpecs"`
Expected: PASS (the 3 existing + the new concurrency test = 4).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestWriter.cs DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestWriterSpecs.cs
git commit -m "Resilient #3: serialize ManifestWriter for concurrent per-channel checkpoint writes"
```

---

### Task 2: `ManifestResume.AlreadyExported` (pure resume helper)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestResume.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestResumeSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestResumeSpecs.cs`:

```csharp
using System;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestResumeSpecs
{
    private static ManifestEntry Entry(string file) =>
        new("1", "g", "2", "c", null, file, "Json", 0, null, null, null, null, null, 0, "x", false, DateTimeOffset.UnixEpoch);

    private static ExportManifest Manifest(params string[] files) =>
        new(ExportManifest.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, Array.ConvertAll(files, Entry));

    [Fact]
    public void Returns_the_candidates_that_are_already_in_the_manifest()
    {
        var done = ManifestResume.AlreadyExported(Manifest("a.json", "b.json"), ["a.json", "c.json"]);

        done.Should().BeEquivalentTo(["a.json"]);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var done = ManifestResume.AlreadyExported(Manifest("Server - General [22].json"), ["server - general [22].json"]);

        done.Should().HaveCount(1);
    }

    [Fact]
    public void A_null_manifest_means_nothing_is_already_exported()
    {
        var done = ManifestResume.AlreadyExported(null, ["a.json", "b.json"]);

        done.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestResumeSpecs"`
Expected: FAIL — `ManifestResume` does not exist.

- [ ] **Step 3: Implement**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestResume.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestResume
{
    // Given a directory's manifest (or null) and a set of candidate output file names, returns the
    // subset of candidates already present in the manifest — i.e. already exported, skippable on resume.
    // Match is by file name, case-insensitive, consistent with the manifest's one-entry-per-file grain.
    public static IReadOnlySet<string> AlreadyExported(
        ExportManifest? manifest,
        IEnumerable<string> candidateFileNames
    )
    {
        var have =
            manifest is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : manifest.Entries.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidateFileNames.Where(have.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestResumeSpecs"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestResume.cs DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestResumeSpecs.cs
git commit -m "Resilient #3: add pure ManifestResume.AlreadyExported helper + tests"
```

---

### Task 3: GUI — per-channel checkpoint, resume prompt, failure tracking

This refactors `ExportAsync` to extract a reusable `RunExportCoreAsync` (also used by Task 4's retry), writes the manifest per channel, adds the resume overlap prompt, and tracks failed channels.

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

- [ ] **Step 1: Add localization strings**

In `LocalizationManager.cs` (Dashboard region, near the export strings) add:

```csharp
    public string ResumePromptTitle => Get();
    public string ResumePromptMessage => Get();
    public string ResumeSkipButton => Get();
    public string ResumeExportAllButton => Get();
    public string ResumeAllUpToDateMessage => Get();
    public string ResumeSkippedMessage => Get();
    public string RetryFailedTooltip => Get();
```

In `LocalizationManager.English.cs` (Dashboard section) add:

```csharp
            [nameof(ResumePromptTitle)] = "Resume export?",
            [nameof(ResumePromptMessage)] =
                "{0} of the selected channels are already exported in this folder. Skip them and export only the rest?",
            [nameof(ResumeSkipButton)] = "SKIP DONE",
            [nameof(ResumeExportAllButton)] = "EXPORT ALL",
            [nameof(ResumeAllUpToDateMessage)] =
                "All selected channels are already exported in this folder.",
            [nameof(ResumeSkippedMessage)] = "Skipped {0} already-exported channel(s).",
            [nameof(RetryFailedTooltip)] = "Retry the channels that failed in the last export",
```

- [ ] **Step 2: Add fields for retry state**

In `DashboardViewModel.cs`, near the other private fields (e.g. after `private DiscordClient? _discord;`), add:

```csharp
    private ExportSetupViewModel? _lastExportSetup;
    private IReadOnlyList<Channel> _lastFailedChannels = [];
```

Add `using DiscordChatExporter.Core.Discord.Data;` if `Channel` isn't already imported (it is — used elsewhere).

- [ ] **Step 3: Replace `ExportAsync` and add `RunExportCoreAsync` + helpers**

Replace the **entire** `ExportAsync` method (the `[RelayCommand(CanExecute = nameof(CanExport))] private async Task ExportAsync()` body) with the following two methods. This preserves the copy-user-messages branch, moves the export core into `RunExportCoreAsync`, adds the resume check, switches to per-channel manifest writes, and records failures:

```csharp
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        IsBusy = true;
        _etaEstimator.Reset();

        try
        {
            if (_discord is null || SelectedGuild is null || !SelectedChannels.Any())
                return;

            var dialog = _viewModelManager.GetExportSetupViewModel(
                SelectedGuild,
                SelectedChannels.Select(c => c.Channel).ToArray()
            );

            if (await _dialogManager.ShowDialogAsync(dialog) != true)
                return;

            var exporter = new ChannelExporter(_discord);

            if (dialog.ShouldCopyUserMessages)
            {
                await CopyUserMessagesAsync(dialog, exporter);
                return;
            }

            var channels = dialog.Channels!.ToArray();
            var failed = await RunExportCoreAsync(exporter, dialog, channels);

            _lastExportSetup = dialog;
            _lastFailedChannels = failed;
            RetryFailedExportCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            IsBusy = false;
            EtaText = null;
        }
    }

    // Builds an ExportRequest for a channel using the dialog's parameters. Pure path-building.
    private ExportRequest BuildExportRequest(ExportSetupViewModel dialog, Channel channel) =>
        new(
            dialog.Guild!,
            channel,
            dialog.OutputPath!,
            dialog.AssetsDirPath,
            dialog.SelectedFormat,
            dialog.After?.Pipe(Snowflake.FromDate),
            dialog.Before?.Pipe(Snowflake.FromDate),
            dialog.PartitionLimit,
            dialog.MessageFilter,
            dialog.IsReverseMessageOrder,
            dialog.ShouldFormatMarkdown,
            dialog.ShouldDownloadAssets,
            dialog.ShouldReuseAssets,
            _settingsService.Locale,
            _settingsService.IsUtcNormalizationEnabled
        );

    private static ManifestChannelInfo BuildManifestInfo(ExportRequest r) =>
        new(
            r.Guild.Id.ToString(),
            r.Guild.Name,
            r.Channel.Id.ToString(),
            r.Channel.Name,
            r.Channel.Parent?.Name,
            r.Format.ToString()
        );

    // Best-effort per-channel manifest checkpoint. Failure must never fail the channel's export.
    private async ValueTask CheckpointManifestAsync(ExportRequest request, ExportResult result)
    {
        try
        {
            var entries = ManifestBuilder.Build(BuildManifestInfo(request), result, DateTimeOffset.Now);
            await ManifestWriter.WriteAsync(request.OutputDirPath, entries, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _snackbarManager.Notify(LocalizationManager.ExportCatalogWriteFailedMessage.TrimEnd('.'));
        }
    }

    // The export core, shared by ExportAsync and the retry command. Returns the channels that failed.
    private async Task<IReadOnlyList<Channel>> RunExportCoreAsync(
        ChannelExporter exporter,
        ExportSetupViewModel dialog,
        IReadOnlyList<Channel> channels
    )
    {
        var requests = channels
            .Select(c => (Channel: c, Request: BuildExportRequest(dialog, c)))
            .ToArray();

        // Resume detection: which selected channels are already exported in their target directory?
        var manifestsByDir = new Dictionary<string, ExportManifest?>(StringComparer.OrdinalIgnoreCase);
        var alreadyDone = new List<(Channel Channel, ExportRequest Request)>();

        foreach (var r in requests)
        {
            var dir = r.Request.OutputDirPath;
            if (!manifestsByDir.TryGetValue(dir, out var manifest))
            {
                manifest = await ManifestReader.TryReadAsync(
                    Path.Combine(dir, ExportManifest.FileName)
                );
                manifestsByDir[dir] = manifest;
            }

            if (
                ManifestResume
                    .AlreadyExported(manifest, [Path.GetFileName(r.Request.OutputFilePath)])
                    .Count > 0
            )
            {
                alreadyDone.Add(r);
            }
        }

        var toExport = requests;

        if (alreadyDone.Count == requests.Length)
        {
            // Everything is already exported here — nothing to do.
            _snackbarManager.Notify(LocalizationManager.ResumeAllUpToDateMessage.TrimEnd('.'));
            return [];
        }

        if (alreadyDone.Count > 0)
        {
            var prompt = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ResumePromptTitle,
                string.Format(LocalizationManager.ResumePromptMessage, alreadyDone.Count),
                LocalizationManager.ResumeSkipButton, // default -> true -> skip (resume)
                LocalizationManager.ResumeExportAllButton // cancel -> false/null -> export all
            );

            if (await _dialogManager.ShowDialogAsync(prompt) == true)
            {
                var doneSet = alreadyDone.Select(d => d.Channel).ToHashSet();
                toExport = requests.Where(r => !doneSet.Contains(r.Channel)).ToArray();
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ResumeSkippedMessage, alreadyDone.Count)
                );
            }
        }

        var pairs = toExport
            .Select(r => new
            {
                r.Channel,
                r.Request,
                Progress = _progressMuxer.CreateInput(),
            })
            .ToArray();

        var exportStats = new ConcurrentBag<ChannelExportStats>();
        var failedChannels = new ConcurrentBag<Channel>();
        var successfulExportCount = 0;
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _settingsService.ParallelLimit) },
            async (pair, cancellationToken) =>
            {
                var request = pair.Request;
                var progress = pair.Progress;

                try
                {
                    var result = await exporter.ExportChannelAsync(request, progress, cancellationToken);

                    await CheckpointManifestAsync(request, result);

                    exportStats.Add(
                        new ChannelExportStats(
                            result.MessageCount,
                            result.AssetCount,
                            SumFileSizes(result.Files)
                        )
                    );

                    Interlocked.Increment(ref successfulExportCount);
                }
                catch (ChannelEmptyException ex)
                {
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));

                    // Empty channels still produce an (empty) file via exporter disposal; checkpoint it
                    // so it counts as "done" for resume, consistent with the filtered-to-empty case.
                    await CheckpointManifestAsync(
                        request,
                        new ExportResult([new ExportedFile(request.OutputFilePath, 0, null, null, null, null)], 0, 0)
                    );
                }
                catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                {
                    failedChannels.Add(pair.Channel);
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                }
                finally
                {
                    progress.ReportCompletion();
                }
            }
        );

        stopwatch.Stop();

        if (successfulExportCount > 0)
        {
            var summary = ExportSummarizer.Summarize(
                exportStats.ToArray(),
                failedChannels.Count,
                stopwatch.Elapsed
            );

            _snackbarManager.Notify(FormatExportSummary(summary));
        }

        CompletionAttention.FlashIfUnfocused();

        return failedChannels.ToArray();
    }
```

Notes for the implementer:
- This **replaces** the old after-loop group-by-dir manifest block — the manifest is now written per channel via `CheckpointManifestAsync`. Make sure the old block (the `if (!manifestData.IsEmpty) { ... }` section) is gone and `manifestData` is no longer referenced.
- `SumFileSizes`, `FormatExportSummary`, `FormatBytes` (from #6) and `BuildExportRequest`/`BuildManifestInfo`/`CheckpointManifestAsync`/`RunExportCoreAsync` (new) all live on the VM. Keep the #6 helpers.
- Add any missing usings: `System.Collections.Generic`, `System.Text.Json` (for the JsonException filter — already added in #6), `DiscordChatExporter.Core.Exporting.Manifest` (already present). `Channel` is `DiscordChatExporter.Core.Discord.Data.Channel`.

- [ ] **Step 4: Build the GUI**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj`
Expected: Build succeeded, 0 errors. If `RetryFailedExportCommand` is reported as not existing, that's expected — it's added in Task 4; temporarily comment out the two lines referencing `_lastExportSetup`/`_lastFailedChannels`/`RetryFailedExportCommand.NotifyCanExecuteChanged()` in `ExportAsync` is NOT allowed — instead, do Task 4 before building, OR add a stub. **Simplest: implement Task 4's command in the same pass before building.** (The two tasks are split for review clarity but the `RetryFailedExportCommand` reference couples them; build after Task 4.)

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Resilient #3: per-channel manifest checkpoint + resume prompt + failure tracking"
```

(If you implement Task 4 in the same pass to satisfy the build, include its files in this commit or commit Task 4 separately after — either is fine as long as each commit builds or the pair builds together. Prefer committing Task 3 + Task 4 together if the build coupling forces it.)

---

### Task 4: "Retry failed (N)" command + FAB

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml`

- [ ] **Step 1: Add the command**

In `DashboardViewModel.cs`, add near `ExportAsync`:

```csharp
    private bool CanRetryFailedExport() =>
        !IsBusy && _discord is not null && _lastExportSetup is not null && _lastFailedChannels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailedExport))]
    private async Task RetryFailedExportAsync()
    {
        if (_discord is null || _lastExportSetup is null || _lastFailedChannels.Count == 0)
            return;

        IsBusy = true;
        _etaEstimator.Reset();

        try
        {
            var exporter = new ChannelExporter(_discord);
            var failed = await RunExportCoreAsync(exporter, _lastExportSetup, _lastFailedChannels);
            _lastFailedChannels = failed;
            RetryFailedExportCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            IsBusy = false;
            EtaText = null;
        }
    }

    public bool HasFailedExport => _lastFailedChannels.Count > 0;
```

Also wire `IsBusy` to refresh the retry command: in the `[ObservableProperty] ... public partial bool IsBusy` attribute list, add `[NotifyCanExecuteChangedFor(nameof(RetryFailedExportCommand))]`. And after `_lastFailedChannels = ...` assignments (both in `ExportAsync` and `RetryFailedExportAsync`), add `OnPropertyChanged(nameof(HasFailedExport));` so the FAB visibility updates.

- [ ] **Step 2: Add a "Retry failed" FAB**

In `DashboardView.axaml`, find the existing Continue-export FAB (the floating action button bound to `ContinueExportCommand`). Add a sibling button next to it, bound to `RetryFailedExportCommand`, visible only when there are failures:

```xml
                <Button
                    Command="{Binding RetryFailedExportCommand}"
                    IsVisible="{Binding HasFailedExport}"
                    Theme="{DynamicResource MaterialFloatingActionButton}"
                    ToolTip.Tip="{Binding LocalizationManager.RetryFailedTooltip}">
                    <icons:MaterialIcon Kind="Refresh" />
                </Button>
```

Match the exact wrapping/style/namespace (`icons:` prefix, button theme) of the existing Continue-export FAB — copy its element and change `Command`, `IsVisible`, `ToolTip.Tip`, and the icon `Kind` to `Refresh`. Place it adjacent to the Continue FAB in the same container.

- [ ] **Step 3: Build the GUI**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj`
Expected: Build succeeded, 0 errors. (This also resolves the `RetryFailedExportCommand` reference from Task 3.)

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Views/Components/DashboardView.axaml
git commit -m "Resilient #3: add Retry-failed command + FAB"
```

---

### Task 5: Full verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build DiscordChatExporter.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the manifest + resume tests**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Manifest"`
Expected: PASS (9 from #1 + 1 new writer concurrency + 3 resume = 13).

- [ ] **Step 3: Regression — everything else still green**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation|FullyQualifiedName~Eta|FullyQualifiedName~ExportSummarizer"`
Expected: PASS (39 + 2 = 41).

---

## Self-Review

**Spec coverage:** per-channel checkpoint (live, crash-surviving) ✔ (Task 1 thread-safe write + Task 3 `CheckpointManifestAsync` per channel); resume / skip-already-exported ✔ (Task 2 helper + Task 3 prompt); retry failures ✔ (Task 3 tracking + Task 4 command/FAB); resilient batch (one failure doesn't abort) ✔ (pre-existing per-channel catch, preserved).

**Invariant protected:** Task 1's concurrency test guards "no lost checkpoint entries"; Task 2's tests guard the skip-set computation. The full IFF invariant (in-manifest ⟺ completed) is exercised by the user's manual smoke (needs token).

**Manual smoke (user-run, needs token):** export a server to a folder; kill the app mid-run; reopen, Select-all → Export to the same folder → confirm the prompt offers SKIP DONE and that skipping re-exports only the channels not yet in `manifest.json`; force a channel failure (e.g. a permissions-restricted channel) → confirm "Retry failed" FAB appears and re-runs just that channel.

**Type consistency:** `RunExportCoreAsync(exporter, dialog, channels)` returns `IReadOnlyList<Channel>`, consumed by both `ExportAsync` and `RetryFailedExportAsync`; `_lastExportSetup`/`_lastFailedChannels` types match; `ManifestResume.AlreadyExported` signature matches the Task 3 call; `CheckpointManifestAsync` reuses `ManifestBuilder.Build`/`ManifestWriter.WriteAsync` from #1.

**Build coupling note:** Task 3 references `RetryFailedExportCommand` (generated by Task 4's `[RelayCommand]`), so the GUI build only succeeds once Task 4 is also in. Implement both before the first GUI build; commit separately if clean, or together if the coupling requires.
