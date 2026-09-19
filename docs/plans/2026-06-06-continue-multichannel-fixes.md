# Multi-channel Continue — 4-fix implementation plan

**Goal:** Make the GUI "Continue export over selected channels" feature (commit `f0f9081`) work smoothly: genuinely one-click, robust file discovery, per-channel failure isolation, and actually tested.

**Source:** Locked design from the 4-agent `continue-fixes` team (ux-flow / discovery / resilience / testing), 2026-06-06. All four converged; this is the single source of truth.

**Constraints:** .NET 10, `TreatWarningsAsErrors=true`, CSharpier on build. Build must stay `0/0`. All new tests token-free.

---

## Architecture: two-stage, catalog-driven, one-click

`Select All → Continue` resumes every selected channel with **no dialog** on the happy path. Each selected channel's most-recent existing export is found via the manifest catalog (`KnownExportDirs`), resumed, failures isolated, one summary toast. The file picker survives **only** as a fallback when the catalog resolves *zero* channels — and that picked file is continued **directly** (uncatalogued folders have no manifest to re-resolve).

---

## Issue #2 — Discovery (new Core file, pure, testable)

`DiscordChatExporter.Core/Exporting/Library/ContinueExportDiscovery.cs`:

```csharp
namespace DiscordChatExporter.Core.Exporting.Library;

public static class ContinueExportDiscovery
{
    public static ValueTask<ContinueDiscoveryResult> ResolveAsync(
        IReadOnlyList<string> directories,
        IReadOnlyCollection<Snowflake> selectedChannelIds,
        CancellationToken ct = default);
}

public sealed record ResolvedCatalogEntry(Snowflake ChannelId, string FilePath, ExportFormat Format);
public sealed record UnresolvedCatalogChannel(Snowflake ChannelId, ContinueSkipReason Reason);
public sealed record ContinueDiscoveryResult(
    IReadOnlyList<ResolvedCatalogEntry> Resolved,
    IReadOnlyList<UnresolvedCatalogChannel> Unresolved);

// ONE shared enum. Stage 1 (discovery) emits the first five; Stage 2 (loop/outer) emits ReverseChronological.
public enum ContinueSkipReason { NoPriorExport, FileMissing, Partitioned, UnsupportedFormat, UnknownFormat, ReverseChronological }
```

Algorithm (per selected channel id):
1. `var entries = await ExportCatalogBuilder.BuildFromDirectoriesAsync(directories, ct);` — **reuse** the existing aggregator (reads each dir's `ExportManifest.FileName` via `ManifestReader.TryReadAsync`, resolves bare `entry.File` → absolute, dedups by path, sorts `ExportedAt` DESC). Do NOT re-walk manifests.
2. `candidates = entries.Where(e => e.ChannelId == id.ToString())` (already newest-first).
3. First **usable** candidate → `ResolvedCatalogEntry(id, e.FilePath, fmt)` where usable = `!e.Partitioned && ContinuationFormat.IsSupportedExtension(e.FilePath) && Enum.TryParse<ExportFormat>(e.Format, out fmt) && File.Exists(e.FilePath)`.
   - **Format from the manifest string `e.Format`, not the extension** (preserves HtmlLight vs HtmlDark).
   - **DECISION (ratified): most-recent-USABLE fallback** — if newest is partitioned/unsupported but an older export is usable, fall back to the older usable one.
4. No candidates → `Unresolved(id, NoPriorExport)`. Candidates but none usable → most-specific reason (`Partitioned` / `UnsupportedFormat` / `UnknownFormat` / `FileMissing`).

Pure: manifest read + `File.Exists` only, no network, no Discord types, no file-content open. `directories` is always `KnownExportDirs` (single call per Continue).

---

## Issue #3 — Resilience: isolation + combined progress + one summary

### Concurrency — SEQUENTIAL `foreach`
Sequential `foreach` over the targets. **Rationale (reverses an earlier parallel recommendation):** continue is **incremental** (small per-channel deltas since each cutoff) and Discord rate limits are **token-global**, so parallel fetches contend on one budget and buy no rate win — only a speculative latency win for the rare many-channel/large-delta case, which doesn't justify the concurrency machinery or the nondeterministic fatal-abort test it forces. Sequential keeps every feature goal (isolation, combined progress, summary, ct-fix) and still mirrors `RunExportCoreAsync`'s reusable **shape** (per-channel index, progress, isolation, summary) — "one pattern" is the shape, not the scheduler. If continue's real usage turns out latency-bound on many channels, this is a localized swap back to `Parallel.ForEachAsync` (re-add the thread-safety proof + set-based tests then).

**FIX (still required):** hoist `RegisterExportedDirs` OUT of `RefreshContinuedExportCatalogAsync` (currently ~:980 — does `KnownExportDirs` read-append-`Save()` per channel = N redundant disk writes). The loop collects dirs in a plain `HashSet<string>(StringComparer.OrdinalIgnoreCase)` and calls `RegisterExportedDirs(set)` **once** post-loop (mirrors export ~:1178). `RefreshContinuedExportCatalogAsync` just refreshes the manifest + returns its bool. (Sequential ⇒ no race; this is a redundant-write fix, not a concurrency fix — no `ManifestWriter` thread-safety reasoning needed.)

### Outer command (`ContinueExportAsync`, after discovery returns)
1. **Read all cutoffs up front** (one `ReadCutoffAsync` per resolved file). MANDATORY — the per-file request's `After`/`Before` come from the cutoff and the combined-progress estimate runs once before the loop; lazy reads ⇒ `After=null` ⇒ whole-channel count ⇒ broken bar.
2. Partition `!cutoff.IsChronological` → reverse channels go straight into the unresolved set (never enter the loop). *This is why there is no `Skip` field — reverse was the only runtime skip.*
3. Map `target.ChannelId → Channel` via `SelectedChannels` (no `GetChannelAsync`).
4. Build pairs `{ Index, Target, Cutoff, Progress = _progressMuxer.CreateInput() }`; `StartExportProgressRun(await EstimateMessageTotalsAsync(requests))` **once**.
5. Build the production `processOne` delegate closing over each pair's `(Index, Progress, Cutoff)`; its `finally` does `MarkExportProgressCompleted(Index)` + `progress.ReportCompletion()` (runs on success AND throw → failed channel advances the bar, doesn't hang it).
6. `var summary = RunContinueLoopAsync(targets, processOne);`
7. `_snackbarManager.Notify(FormatContinueSummary(summary));` + catalog-failed once; final unresolved set = `discovery.Unresolved` + reverse-partitioned, for the toast tail.

### Inner (testable seam, free of `_progressMuxer`/estimate)
```csharp
async Task<ContinueExportRunSummary> RunContinueLoopAsync(
    IReadOnlyList<ResolvedContinueTarget> targets,
    Func<ResolvedContinueTarget, CancellationToken, Task<ContinueExportFileResult>> processOne,
    CancellationToken ct)
{
    var processed = new List<ContinueExportFileResult>();
    var failedChannels = new List<Channel>();
    var exportedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var catalogWriteFailed = false;
    foreach (var target in targets)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var r = await processOne(target, ct);
            if (r.WasProcessed)
            {
                processed.Add(r);
                if (!r.IsCatalogRefreshed) catalogWriteFailed = true;
                exportedDirs.Add(target.Dir);
            }
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            failedChannels.Add(target.Channel);
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        // fatal bubbles → outer catch in ContinueExportAsync (same as RunExportCoreAsync)
    }
    RegisterExportedDirs(exportedDirs.ToArray());
    return new ContinueExportRunSummary(processed.Count, processed.Sum(p => p.NewMessages), catalogWriteFailed, failedChannels);
}
```

### Types (internal + `[InternalsVisibleTo("DiscordChatExporter.Gui.Tests")]`)
```csharp
record ContinueExportFileResult(bool WasProcessed, long NewMessages, bool IsCatalogRefreshed) { public static ... Skipped = new(false, 0, true); } // NO Skip/Failed field
record ContinueExportRunSummary(int ProcessedCount, long TotalNewMessages, bool CatalogWriteFailed, IReadOnlyList<Channel> FailedChannels);
record ResolvedContinueTarget(Channel Channel, Guild Guild, string FilePath, string Dir, ExportFormat Format, ContinuationCutoff Cutoff); // VM-side hydration of discovery's id-records
```
Production `processOne` body = today's `ContinueExportFileAsync` logic: build request from **`target.Format`** (not `FormatFor(path)`); `ChannelEmptyException` handled **inside** (non-Db → `(true,0,true)`, Db → fall through to merge); merge; delta; `RefreshContinuedExportCatalogAsync`; `finally` temp-delete + progress-complete.

### `FormatContinueSummary(ContinueExportRunSummary)` (pure, mirrors `FormatExportSummary`)
`ProcessedCount==0` → nothing; `TotalNewMessages<=0` → `ContinueExportUpToDateMessage`; else `ContinueExportSuccessMessage(TotalNewMessages)`; `FailedChannels.Count>0` → append failed tail; `CatalogWriteFailed` → `ExportCatalogWriteFailedMessage` once.

---

## Issue #1 — UX (DashboardViewModel changes)
- Rewrite `ContinueExportAsync` pre-loop: drop the upfront picker; call `ContinueExportDiscovery.ResolveAsync(_settingsService.KnownExportDirs, selectedIds)`; if `ShouldPromptAnchorPicker(result)` → legacy picker fallback (picked file continued **directly**, picker-cancel = silent return); else loop, no dialog.
- `private static bool ShouldPromptAnchorPicker(ContinueDiscoveryResult r) => r.Resolved.Count == 0;`
- **Delete** `ResolveSelectedContinueExportFilePaths` (fragile default-filename guesser).
- `CanContinueExport` UNCHANGED (keep the FAB-gating guard test).
- Localization: reword `ContinueExportTooltip` (drop single-file implication); retire `ContinueExportNoSelectedFilesMessage` (cancel now silent); add `ContinueExportSkippedTail` "{0} had no existing export, skipped" + `ContinueExportFailedTail` "{0} failed".

---

## Issue #4 — Tests (~16–18, token-free; only gating test uses `[AvaloniaFact]`)

**A) Discovery** — `Cli.Tests/Specs/Library/ContinueExportDiscoverySpecs.cs`, `[Fact]`, temp-dir manifest fixtures (mirror `ExportCatalogBuilderSpecs`; `ManifestEntry` = 17 fields, `File`=bare name, `Format`=enum string):
1. `Resolves_single_export_with_absolute_path_and_format`
2. `Resolves_exact_format_from_catalog_not_extension` — **FLAGSHIP** (`"HtmlLight"` on `.html` → `HtmlLight`)
3. `Picks_most_recent_export_by_ExportedAt_across_dirs`
4. `Skips_partitioned_only_export_as_Unresolved_Partitioned`
5. `Skips_channel_with_no_prior_export_as_Unresolved_NoPriorExport`
6. `Skips_entry_whose_file_is_missing_as_Unresolved_FileMissing`
7. `Skips_unsupported_or_unparseable_format_as_Unresolved`
8. `Partial_resolution_returns_resolved_and_unresolved_disjoint_sets`

**B) Loop** — `Gui.Tests/ContinueExportRunnerTests.cs`, `[Fact]`, recording fake `processOne`. Sequential loop ⇒ **deterministic** asserts (observed order is stable):
9. `Nonfatal_failure_isolates_that_channel_and_others_still_run` (all non-failing targets observed; `failedChannels == {that one}`; summary totals exclude it)
10. `Fatal_failure_aborts_loop_and_skips_remaining` (fatal on target 2 → propagates `DiscordChatExporterException` with `IsFatal`; target 3 **never** observed — deterministic under sequential)
11. `New_message_totals_summed_across_channels`

**C) Reducer/Summary** — same file, `[Fact]`: `SummarizeContinue_aggregates_processed_and_passes_through_failed`, `SummarizeContinue_empty_is_all_zero`, `Up_to_date_when_processed_but_zero_new`, `Reports_new_message_count_when_positive`.

**E) One-click** — `Gui.Tests/DashboardContinueOneClickTests.cs`, `[Fact]`: `Does_not_prompt_picker_when_catalog_resolves_at_least_one_channel`, `Prompts_picker_when_catalog_resolves_zero_channels`.

**F) Remove** the obsolete `ResolveSelectedContinueExportFilePaths` test from `DashboardContinueExportPickerTests`.

**DECISION (ratified): predicate-only one-click tests** — the two `[AvaloniaFact]` wiring guards are dropped (driving real `ContinueExportAsync` headlessly fires live HTTP via the fake-token `_discord`); no `DialogManager`/`continueOne` production seams needed.

Fakes: `RecordingProcessOne` (~20 lines, `ConcurrentBag` of observed targets), manifest fixture writer (~20 lines). No mocking framework, no token.

---

## Bonus bugs fixed along the way
1. **HtmlLight → HtmlDark re-theme:** continuing a HtmlLight export today silently re-themes to HtmlDark (`ContinuationFormat.FormatFor` maps `.html → HtmlDark`). Fixed by building the request from the manifest format.
2. **Dropped `CancellationToken`:** today's `ContinueExportFileAsync` (~:1437) does not pass the token to `ExportChannelAsync` (Export passes it). Threaded through `processOne` → `ExportChannelAsync`.

---

## Ratified decisions
1. Fallback when newest export unusable → **most-recent-usable** (not hard-skip).
2. Loop → **sequential `foreach`** (reversed from parallel: parallel's benefit is speculative for incremental continue with token-global rate limits, and it forces nondeterministic tests; sequential is a strict simplification that keeps every feature goal).
3. One-click tests → **predicate-only** (no production testability seams).

## Blast radius
1 new Core file (`ContinueExportDiscovery`), the loop/reducer/summary seam (`RunContinueLoopAsync` + `FormatContinueSummary` + types) on the VM, a rewrite of `ContinueExportAsync`'s pre-loop, deletion of `ResolveSelectedContinueExportFilePaths`, ~4 localization strings, ~16–18 tests. Merge/cutoff/catalog-write internals untouched.
