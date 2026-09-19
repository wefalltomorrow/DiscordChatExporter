# Progress: kill the rate-limit burst, smooth the ETA, show "messages left" — Implementation Plan

> **For agentic workers (Codex):** Implement task-by-task in order. Steps use checkbox (`- [ ]`) syntax. Each code step shows the exact code. Run the listed commands and check the expected output before moving on. Commit after each task.

**Goal:** Stop continue/export from hammering Discord's rate limits before the download starts, keep the time-left estimate smooth even when some channels can't be counted, and display "messages left" (not just messages recorded).

**Architecture:** All three live in the GUI progress/ETA subsystem in `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`. (1) The pre-export estimate stops calling the density-probe fallback (it fired 2–11 message-endpoint requests per channel, pre-spending the export's own rate budget). (2) The count-based progress math becomes robust to *partial* counts instead of reverting the entire bar to the biased timestamp fraction the moment one channel is uncountable. (3) The status text shows remaining = total − read when a total is known.

**Tech Stack:** .NET 10, C#, Avalonia 12 + CommunityToolkit.Mvvm. `TreatWarningsAsErrors=true`; CSharpier formats on build (build fails if unformatted — run the formatter, see "Formatting" below). Tests: `DiscordChatExporter.Gui.Tests` (Avalonia.Headless `[AvaloniaFact]`, reflection-driven private methods — see `DashboardProgressTests.cs`).

**Formatting (do this before every build/commit):** the repo enforces CSharpier 1.2.6. After editing, format with the bundled binary:
```
dotnet "C:\Users\ExampleUser\.nuget\packages\csharpier.msbuild\1.2.6\tools\csharpier\net10.0\CSharpier.dll" format <changed files...>
```
A `dotnet build` will fail with `error : Was not formatted.` if you skip this.

**Out of scope (do NOT do):** Do not delete `DiscordClient.EstimateMessageCountByDensityAsync` or `MessageCountEstimator` — only stop calling density from the estimate path (Task 1). Removing them would cascade into their own test suites; leave that as a separate cleanup.

---

## Task 1: Stop the rate-limit burst — drop the density fallback from the pre-export estimate

**Why:** `EstimateMessageTotalsAsync` runs once before every export/continue. For a continue, the cheap search count (`CountMessagesAsync`, separate rate bucket) usually returns null for the small recent range, so it falls back to `EstimateMessageCountByDensityAsync`, which fires 2–11 requests **on the message endpoint** per channel — the same endpoint the export uses — right before the export. For ~10 channels that's dozens of requests that pre-spend the export's rate budget, so the download starts already throttled. We keep the cheap search count and drop the density burst.

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` (method `EstimateMessageTotalsAsync`, ~line 492)

- [ ] **Step 1: Replace the body of the per-request estimate loop**

Find this (inside `EstimateMessageTotalsAsync`):

```csharp
            var request = requests[i];
            var total =
                await _discord.CountMessagesAsync(request.Channel, request.After, request.Before)
                ?? await _discord.EstimateMessageCountByDensityAsync(
                    request.Channel,
                    request.After,
                    request.Before
                );

            totals[i] = total is > 0 ? total : null;
```

Replace with (drop the `?? EstimateMessageCountByDensityAsync(...)` fallback):

```csharp
            var request = requests[i];
            // Only the cheap search-count call here — it uses a separate rate bucket and one request
            // per channel. The density fallback was removed: it fired up to ~11 message-endpoint
            // requests per channel, pre-spending the export's own rate budget in a burst right before
            // the download (the cause of continue's constant rate-limit pauses). Channels search can't
            // count get a null total; GetCorrectedEstimatedTotal now tolerates that (see Task 2).
            var total = await _discord.CountMessagesAsync(
                request.Channel,
                request.After,
                request.Before
            );

            totals[i] = total is > 0 ? total : null;
```

- [ ] **Step 2: Format, then verify density is no longer called from the estimate path**

Run the formatter (see Formatting above) on `DashboardViewModel.cs`, then:

Run: `grep -n "EstimateMessageCountByDensityAsync" DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
Expected: **no matches** (the GUI no longer calls density).

- [ ] **Step 3: Build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release --nologo`
Expected: `0 Warning(s) 0 Error(s)`.

(No unit test here — `EstimateMessageTotalsAsync` requires a live `DiscordClient`. It's covered by inspection + the grep above. The behavior win is the absence of the pre-export request burst.)

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs
git commit -m "Drop density-probe fallback from pre-export estimate (kills continue rate-limit burst)"
```

---

## Task 2: Partial-count handling — keep the bar count-based when only some channels are counted

**Why:** Today `GetCorrectedEstimatedTotal` returns null if **any** channel estimate is null, and the muxer subscription then reverts `DisplayedProgressFraction` to the biased timestamp fraction. So one uncountable channel (now common after Task 1) sinks the whole bar/ETA into jank. Fix: engage count-based progress whenever **at least one** channel is counted, substituting a fallback total (mean of the counted channels) for the uncounted ones; the existing messages-read correction self-adjusts it.

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` (`HasCompleteCountEstimate` ~line 525, `GetCorrectedEstimatedTotal` ~line 549, the constructor's muxer subscription ~line 92)
- Modify (test): `DiscordChatExporter.Gui.Tests/DashboardProgressTests.cs`

- [ ] **Step 1: Update the failing test to the new expected behavior**

In `DashboardProgressTests.cs`, replace the existing test `Mixed_missing_estimate_leaves_fallback_fraction_owned_by_muxer` (it encodes the old all-or-nothing behavior) with:

```csharp
    [AvaloniaFact]
    public void Mixed_missing_estimate_still_uses_count_based_progress()
    {
        var viewModel = CreateViewModel();

        // One channel counted (2), one uncountable (null). The uncounted channel borrows the
        // counted channel's total as a fallback, so the bar stays count-based instead of reverting
        // to the muxer's timestamp fraction (which would read ~0.25 here).
        Invoke(viewModel, "StartExportProgressRun", new long?[] { 2, null });
        viewModel.Progress.Report(Percentage.FromFraction(0.25));
        Dispatcher.UIThread.RunJobs();

        Invoke(
            viewModel,
            "ApplyExportProgress",
            1,
            new ExportProgress(Percentage.FromFraction(0.9), 1, DateTimeOffset.UnixEpoch)
        );

        // read=1 over a modeled total of ~3 (fallback 2 for the uncounted channel, corrected by the
        // 1 message actually read at fraction 0.9) -> ~0.333, NOT the muxer's 0.25.
        viewModel.DisplayedProgressFraction.Should().BeApproximately(1.0 / 3, 0.02);
    }
```

- [ ] **Step 2: Run the test to verify it FAILS**

Run: `dotnet test DiscordChatExporter.Gui.Tests/DiscordChatExporter.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~Mixed_missing_estimate"`
Expected: FAIL — current code reverts to the muxer fraction (~0.25), so the assertion (~0.333) fails.

- [ ] **Step 3: Rename `HasCompleteCountEstimate` → `HasAnyCountEstimate` and loosen it**

Replace:

```csharp
    private bool HasCompleteCountEstimate()
    {
        lock (_exportProgressLock)
        {
            return _estimatedMessagesByChannel.Length > 0
                && _estimatedMessagesByChannel.All(t => t is not null);
        }
    }
```

with:

```csharp
    private bool HasAnyCountEstimate()
    {
        lock (_exportProgressLock)
        {
            return _estimatedMessagesByChannel.Length > 0
                && _estimatedMessagesByChannel.Any(t => t is not null);
        }
    }
```

- [ ] **Step 4: Update the only caller (constructor muxer subscription)**

In the constructor's `Progress.WatchProperty(o => o.Current, ...)` callback, find:

```csharp
                        if (!_isExportProgressRunActive || !HasCompleteCountEstimate())
                            DisplayedProgressFraction = Progress.Current.Fraction;
```

Replace with:

```csharp
                        if (!_isExportProgressRunActive || !HasAnyCountEstimate())
                            DisplayedProgressFraction = Progress.Current.Fraction;
```

(Confirm there are no other references: `grep -n "HasCompleteCountEstimate" DiscordChatExporter.Gui` should return nothing after this.)

- [ ] **Step 5: Make `GetCorrectedEstimatedTotal` tolerate partial counts**

Replace the whole method:

```csharp
    private long? GetCorrectedEstimatedTotal(long messagesRead)
    {
        if (
            _estimatedMessagesByChannel.Length == 0
            || _estimatedMessagesByChannel.Any(t => t is null)
        )
        {
            return null;
        }

        var modeledWalked = 0.0;
        var modeledRemaining = 0.0;
        for (var i = 0; i < _estimatedMessagesByChannel.Length; i++)
        {
            var estimate = _estimatedMessagesByChannel[i]!.Value;
            var fraction = _completedChannels[i]
                ? 1
                : Math.Clamp(_progressFractionByChannel[i], 0, 1);

            modeledWalked += estimate * fraction;
            modeledRemaining += estimate * (1 - fraction);
        }

        if (messagesRead > 0 && modeledWalked > 0)
        {
            var correction = messagesRead / modeledWalked;
            var correctedTotal = messagesRead + correction * modeledRemaining;
            return Math.Max(messagesRead, (long)Math.Ceiling(correctedTotal));
        }

        return _estimatedMessagesByChannel.Sum(t => t!.Value);
    }
```

with:

```csharp
    private long? GetCorrectedEstimatedTotal(long messagesRead)
    {
        // Engage count-based progress as long as we counted AT LEAST ONE channel. (Reverting the
        // whole bar to the timestamp fraction the moment one channel was uncountable is what made
        // the ETA janky.) Channels we couldn't count borrow a fallback total — the mean of the
        // channels we did count — which the messages-read correction below then self-adjusts.
        if (
            _estimatedMessagesByChannel.Length == 0
            || _estimatedMessagesByChannel.All(t => t is null)
        )
        {
            return null;
        }

        var counted = _estimatedMessagesByChannel
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .ToArray();
        var fallback = counted.Length > 0 ? (long)Math.Ceiling(counted.Average()) : 0;

        var modeledWalked = 0.0;
        var modeledRemaining = 0.0;
        for (var i = 0; i < _estimatedMessagesByChannel.Length; i++)
        {
            // For an uncounted channel, assume the fallback total, but never less than what it has
            // already read (so its contribution can't shrink below reality).
            var estimate =
                _estimatedMessagesByChannel[i] ?? Math.Max(fallback, _messagesReadByChannel[i]);
            var fraction = _completedChannels[i]
                ? 1
                : Math.Clamp(_progressFractionByChannel[i], 0, 1);

            modeledWalked += estimate * fraction;
            modeledRemaining += estimate * (1 - fraction);
        }

        if (messagesRead > 0 && modeledWalked > 0)
        {
            var correction = messagesRead / modeledWalked;
            var correctedTotal = messagesRead + correction * modeledRemaining;
            return Math.Max(messagesRead, (long)Math.Ceiling(correctedTotal));
        }

        // No reads yet: sum counted channels plus the fallback for the uncounted ones.
        long sum = 0;
        for (var i = 0; i < _estimatedMessagesByChannel.Length; i++)
            sum += _estimatedMessagesByChannel[i] ?? fallback;
        return sum;
    }
```

- [ ] **Step 6: Format, then run the updated test + the rest of the progress suite**

Format `DashboardViewModel.cs` and `DashboardProgressTests.cs`, then:

Run: `dotnet test DiscordChatExporter.Gui.Tests/DiscordChatExporter.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~DashboardProgressTests"`
Expected: PASS — all `DashboardProgressTests` pass, including `Mixed_missing_estimate_still_uses_count_based_progress`. (The other progress tests use all-counted estimates, so their behavior is unchanged.)

- [ ] **Step 7: Build the GUI (full CSharpier check) and commit**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release --nologo`
Expected: `0 Warning(s) 0 Error(s)`.

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui.Tests/DashboardProgressTests.cs
git commit -m "Make progress count-based with partial counts (smooth ETA when some channels can't be counted)"
```

---

## Task 3: Show "messages left", not just messages recorded

**Why:** The status line shows only messages recorded. When a total is known (now more often, thanks to Task 2), show remaining = total − read.

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` (`UpdateProgressStatusText` ~line 682 and its two callers `ApplyExportProgress` ~line 620 and `MarkExportProgressCompletedOnUiThread` ~line 657)
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs` and `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`
- Modify (test): `DiscordChatExporter.Gui.Tests/DashboardProgressTests.cs`

- [ ] **Step 1: Add the localization string**

In `LocalizationManager.cs`, next to the existing `public string MessagesReadFormat => Get();`, add:

```csharp
    public string MessagesProgressFormat => Get();
```

In `LocalizationManager.English.cs`, next to the existing `[nameof(MessagesReadFormat)] = ...` entry, add:

```csharp
            [nameof(MessagesProgressFormat)] = "{0} of {1} messages ({2} left)",
```

- [ ] **Step 2: Write the failing tests**

In `DashboardProgressTests.cs`, add:

```csharp
    [AvaloniaFact]
    public void Status_text_shows_messages_left_when_total_is_known()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { 100 });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.1), 10, DateTimeOffset.UnixEpoch)
        );

        // total corrects to 100, read 10 -> 90 left.
        viewModel.MessagesReadText.Should().Contain("90 left");
    }

    [AvaloniaFact]
    public void Status_text_shows_only_read_count_when_total_is_unknown()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", new long?[] { (long?)null });
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.5), 5, DateTimeOffset.UnixEpoch)
        );

        viewModel.MessagesReadText.Should().NotBeNull();
        viewModel.MessagesReadText.Should().NotContain("left");
    }
```

- [ ] **Step 3: Run to verify they FAIL**

Run: `dotnet test DiscordChatExporter.Gui.Tests/DiscordChatExporter.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~Status_text"`
Expected: `Status_text_shows_messages_left_when_total_is_known` FAILS (current text has no "left"); the other passes.

- [ ] **Step 4: Thread the total into the status text**

Change the signature and head of `UpdateProgressStatusText`. Find:

```csharp
    private void UpdateProgressStatusText(long messagesRead, DateTimeOffset? currentTimestamp)
    {
        MessagesReadText =
            messagesRead > 0
                ? string.Format(
                    LocalizationManager.MessagesReadFormat,
                    messagesRead.ToString("N0", CultureInfo.CurrentCulture)
                )
                : null;
```

Replace with:

```csharp
    private void UpdateProgressStatusText(
        long messagesRead,
        long? estimatedTotal,
        DateTimeOffset? currentTimestamp
    )
    {
        MessagesReadText = FormatMessagesRead(messagesRead, estimatedTotal);
```

Then add this helper method directly above `UpdateProgressStatusText`:

```csharp
    private string? FormatMessagesRead(long messagesRead, long? estimatedTotal)
    {
        if (messagesRead <= 0)
            return null;

        var read = messagesRead.ToString("N0", CultureInfo.CurrentCulture);

        // Only show "left" when we actually have a total and it hasn't been overrun.
        if (estimatedTotal is { } total && total >= messagesRead)
        {
            return string.Format(
                LocalizationManager.MessagesProgressFormat,
                read,
                total.ToString("N0", CultureInfo.CurrentCulture),
                (total - messagesRead).ToString("N0", CultureInfo.CurrentCulture)
            );
        }

        return string.Format(LocalizationManager.MessagesReadFormat, read);
    }
```

- [ ] **Step 5: Update the two callers to pass the total**

In `ApplyExportProgress`, find:

```csharp
        UpdateProgressStatusText(messagesRead, currentTimestamp);
```

Replace with:

```csharp
        UpdateProgressStatusText(messagesRead, estimatedTotal, currentTimestamp);
```

In `MarkExportProgressCompletedOnUiThread`, find:

```csharp
        UpdateProgressStatusText(messagesRead, null);
```

Replace with:

```csharp
        UpdateProgressStatusText(messagesRead, estimatedTotal, null);
```

(Both methods already have `estimatedTotal` in scope from the `SnapshotExportProgress()` call above each line.)

- [ ] **Step 6: Format, run the tests, build**

Format the three changed files, then:

Run: `dotnet test DiscordChatExporter.Gui.Tests/DiscordChatExporter.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~DashboardProgressTests"`
Expected: PASS (all progress tests, including the two new `Status_text_*`).

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release --nologo`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs DiscordChatExporter.Gui.Tests/DashboardProgressTests.cs
git commit -m "Show messages-left in export progress when a total is known"
```

---

## Final verification (after all tasks)

- [ ] Full GUI test suite green:

Run: `dotnet test DiscordChatExporter.Gui.Tests/DiscordChatExporter.Gui.Tests.csproj -c Release --nologo`
Expected: all pass (was 35 before this plan; +3 net here — 2 new `Status_text_*` plus the renamed mixed-estimate test).

- [ ] Token-free Core/Cli still green (nothing here should touch it, but confirm):

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~Continuation|FullyQualifiedName~Manifest|FullyQualifiedName~ContinueExportDiscovery"`
Expected: all pass.

- [ ] Build is `0/0`:

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release --nologo`

Report back: files changed, final test counts, and confirmation the build is 0/0. Do not deploy — the human handles deployment.

## Self-review checklist (done while writing this plan)
- **Spec coverage:** rate-limit burst (Task 1), janky ETA / partial counts (Task 2), messages-left (Task 3) — all three of the reported issues have a task.
- **Type consistency:** `HasCompleteCountEstimate` → `HasAnyCountEstimate` renamed with its single caller updated; new `MessagesProgressFormat` declared in both the property file and the English values; `UpdateProgressStatusText` signature change applied at both call sites (both already hold `estimatedTotal`).
- **No placeholders:** every code step shows the full before/after.
