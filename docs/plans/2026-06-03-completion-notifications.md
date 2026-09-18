# Completion Notifications + Summary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a GUI export finishes, show a rich in-app summary (channels · messages · assets · size · duration, and failures when any) and, on Windows, flash the taskbar if the window isn't focused — so long unattended exports get a clear "done" signal and an at-a-glance health check.

**Architecture:** A pure `ExportSummarizer.Summarize(...)` in Core aggregates per-channel stats into an `ExportSummary` (unit-tested). The GUI collects per-successful-channel stats during the export loop, times the run with a `Stopwatch`, summarizes after the loop, renders via the existing `SnackbarManager`, and calls a best-effort `CompletionAttention.FlashIfUnfocused()`. The taskbar flash is `FlashWindowEx` (user32) — no dependency, works for a portable unsigned exe, no-op on non-Windows.

**Tech Stack:** C#/.NET 10, P/Invoke (`LibraryImport`), Avalonia 12. Tests: xUnit + FluentAssertions in `DiscordChatExporter.Cli.Tests` (token-free).

**Design decisions (locked):**
- **No native toast** — WinRT toasts need an AppUserModelID/Start-Menu registration that a portable unsigned exe usually lacks (silently no-ops, unverifiable). Taskbar flash is the robust unattended cue.
- **`ExportSummary` is extensible:** carries `FailedChannels` now (populated from a real failure counter); feature #3 will add per-channel failure detail and the summary string already conditionally shows "· N failed".
- **Succeeded count == `successfulExportCount`** (excludes empty channels). Summary stats are collected only on the success path, NOT from `manifestData` (which includes empty-channel entries).
- **Pure aggregation, build-verified wiring:** `ExportSummarizer` is unit-tested; the flash + snackbar + disk byte-summing are build-verified + the user's manual smoke.

---

### Task 1: `ExportSummary` + pure `ExportSummarizer`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/ExportSummary.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/ExportSummarizerSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/ExportSummarizerSpecs.cs`:

```csharp
using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportSummarizerSpecs
{
    [Fact]
    public void Summarize_totals_messages_assets_and_bytes_across_channels()
    {
        var stats = new[]
        {
            new ChannelExportStats(100, 5, 1000),
            new ChannelExportStats(50, 2, 500),
        };

        var summary = ExportSummarizer.Summarize(stats, failedChannels: 0, duration: TimeSpan.FromSeconds(30));

        summary.SucceededChannels.Should().Be(2);
        summary.FailedChannels.Should().Be(0);
        summary.TotalMessages.Should().Be(150);
        summary.TotalAssets.Should().Be(7);
        summary.TotalBytes.Should().Be(1500);
        summary.Duration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Summarize_passes_through_failed_count_and_handles_empty_input()
    {
        var summary = ExportSummarizer.Summarize([], failedChannels: 3, duration: TimeSpan.Zero);

        summary.SucceededChannels.Should().Be(0);
        summary.FailedChannels.Should().Be(3);
        summary.TotalMessages.Should().Be(0);
        summary.TotalBytes.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ExportSummarizerSpecs"`
Expected: FAIL — `ExportSummarizer`/`ChannelExportStats`/`ExportSummary` do not exist.

- [ ] **Step 3: Implement the records + summarizer**

Create `DiscordChatExporter.Core/Exporting/ExportSummary.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

// Per-channel contribution to the run summary.
public sealed record ChannelExportStats(long MessageCount, long AssetCount, long ByteSize);

// Aggregate outcome of an export run, for the completion summary.
public sealed record ExportSummary(
    int SucceededChannels,
    int FailedChannels,
    long TotalMessages,
    long TotalAssets,
    long TotalBytes,
    TimeSpan Duration
);

public static class ExportSummarizer
{
    public static ExportSummary Summarize(
        IReadOnlyList<ChannelExportStats> succeeded,
        int failedChannels,
        TimeSpan duration
    ) =>
        new(
            SucceededChannels: succeeded.Count,
            FailedChannels: failedChannels,
            TotalMessages: succeeded.Sum(s => s.MessageCount),
            TotalAssets: succeeded.Sum(s => s.AssetCount),
            TotalBytes: succeeded.Sum(s => s.ByteSize),
            Duration: duration
        );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ExportSummarizerSpecs"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/ExportSummary.cs DiscordChatExporter.Cli.Tests/Specs/ExportSummarizerSpecs.cs
git commit -m "Notifications #6: add pure ExportSummarizer + tests"
```

---

### Task 2: Taskbar flash (Windows, best-effort, no dependency)

**Files:**
- Create: `DiscordChatExporter.Gui/Utils/CompletionAttention.cs`

- [ ] **Step 1: Implement the flasher + window-resolution wrapper**

Create `DiscordChatExporter.Gui/Utils/CompletionAttention.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using DiscordChatExporter.Gui.Utils.Extensions;

namespace DiscordChatExporter.Gui.Utils;

// Best-effort "export finished" attention cue. On Windows, flashes the taskbar button when the
// main window isn't focused; no-op everywhere else. No dependency, works for a portable exe.
internal static class CompletionAttention
{
    public static void FlashIfUnfocused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Only signal if the user isn't already looking at the window.
        if (
            Application.Current?.ApplicationLifetime?.TryGetTopLevel() is not Window window
            || window.IsActive
        )
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        try
        {
            NativeMethods.FlashTaskbar(handle.Value);
        }
        catch
        {
            // Best-effort: an attention cue must never disrupt the app.
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-flashwinfo
        private const uint FLASHW_TRAY = 0x00000002; // flash the taskbar button
        private const uint FLASHW_TIMERNOFG = 0x0000000C; // flash until the window comes to the foreground

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

        public static void FlashTaskbar(IntPtr hwnd)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };

            FlashWindowEx(ref info);
        }
    }
}
```

Note: `NativeMethods` must be `partial` because `[LibraryImport]` generates the P/Invoke body. The enclosing `CompletionAttention` does not need to be partial.

- [ ] **Step 2: Build the GUI**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj`
Expected: Build succeeded, 0 errors. (If the analyzer complains that the `FlashTaskbar` call site needs an OS guard, it is already guarded by the `OperatingSystem.IsWindows()` early-return in `FlashIfUnfocused`; the `[SupportedOSPlatform("windows")]` on `NativeMethods` documents this.)

- [ ] **Step 3: Commit**

```bash
git add DiscordChatExporter.Gui/Utils/CompletionAttention.cs
git commit -m "Notifications #6: add Windows taskbar-flash attention cue (no-op elsewhere)"
```

---

### Task 3: Wire summary + flash into the GUI export

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

- [ ] **Step 1: Add localization strings**

In `LocalizationManager.cs`, in the Dashboard region (near `ExportCatalogWriteFailedMessage`), add:

```csharp
    public string ExportSummaryMessage => Get();
    public string ExportSummaryFailedSuffix => Get();
```

In `LocalizationManager.English.cs`, in the Dashboard section (near `[nameof(ExportCatalogWriteFailedMessage)]`), add:

```csharp
            [nameof(ExportSummaryMessage)] =
                "Exported {0} channel(s) · {1} message(s) · {2} asset(s) · {3} · {4}",
            [nameof(ExportSummaryFailedSuffix)] = " · {0} failed",
```

- [ ] **Step 2: Add usings**

In `DashboardViewModel.cs`, add to the using block (if not already present):

```csharp
using System.Diagnostics;
using System.Globalization;
using DiscordChatExporter.Gui.Utils;
```

- [ ] **Step 3: Collect stats, time the run, summarize, notify, flash**

In `ExportAsync`, locate the block that currently declares `manifestData` (added by feature #1) and the `successfulExportCount`. Add, right after the `manifestData` declaration:

```csharp
            var exportStats = new ConcurrentBag<ChannelExportStats>();
            var failedExportCount = 0;
            var stopwatch = Stopwatch.StartNew();
```

In the `Parallel.ForEachAsync` body, in the success path (right after the existing `manifestData.Add(...)` and before/around `Interlocked.Increment(ref successfulExportCount);`), add the per-channel stat collection:

```csharp
                        exportStats.Add(
                            new ChannelExportStats(
                                result.MessageCount,
                                result.AssetCount,
                                SumFileSizes(result.Files)
                            )
                        );

                        Interlocked.Increment(ref successfulExportCount);
```

In the `catch (DiscordChatExporterException ex) when (!ex.IsFatal)` block inside the loop, add a failure count (keep the existing notify):

```csharp
                    catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                    {
                        Interlocked.Increment(ref failedExportCount);
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                    }
```

Then replace the existing overall-completion notify block:

```csharp
            // Notify of the overall completion
            if (successfulExportCount > 0)
            {
                _snackbarManager.Notify(
                    string.Format(
                        LocalizationManager.SuccessfulExportMessage,
                        successfulExportCount
                    )
                );
            }
```

with:

```csharp
            // Notify of the overall completion with a summary
            stopwatch.Stop();
            if (successfulExportCount > 0)
            {
                var summary = ExportSummarizer.Summarize(
                    exportStats.ToArray(),
                    failedExportCount,
                    stopwatch.Elapsed
                );

                _snackbarManager.Notify(FormatExportSummary(summary));
            }

            // Flash the taskbar if the user isn't watching (best-effort, Windows only)
            CompletionAttention.FlashIfUnfocused();
```

(Place this after the manifest-write block from feature #1, or before it — order doesn't matter, but keep the `stopwatch.Stop()` before reading `Elapsed`.)

- [ ] **Step 4: Add the formatting helpers**

In `DashboardViewModel.cs`, near the existing `FormatDuration` helper, add:

```csharp
    private string FormatExportSummary(ExportSummary summary)
    {
        var message = string.Format(
            LocalizationManager.ExportSummaryMessage,
            summary.SucceededChannels,
            summary.TotalMessages.ToString("N0", CultureInfo.CurrentCulture),
            summary.TotalAssets.ToString("N0", CultureInfo.CurrentCulture),
            FormatBytes(summary.TotalBytes),
            FormatDuration(summary.Duration)
        );

        if (summary.FailedChannels > 0)
            message += string.Format(LocalizationManager.ExportSummaryFailedSuffix, summary.FailedChannels);

        return message;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes} B";

    private static long SumFileSizes(IReadOnlyList<ExportedFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                total += new FileInfo(file.FilePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a size we can't read just doesn't contribute to the total.
            }
        }

        return total;
    }
```

(`IReadOnlyList<ExportedFile>` needs `using System.Collections.Generic;` — already present — and `ExportedFile` from `DiscordChatExporter.Core.Exporting` — already imported.)

- [ ] **Step 5: Build the GUI**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Notifications #6: show export summary + taskbar flash on completion"
```

---

### Task 4: Full verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build DiscordChatExporter.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the summarizer tests**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ExportSummarizerSpecs"`
Expected: PASS (2 tests).

- [ ] **Step 3: Regression — manifest + continuation + eta still green**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Manifest|FullyQualifiedName~Continuation|FullyQualifiedName~Eta"`
Expected: PASS (9 manifest + 39 continuation/eta = 48).

---

## Self-Review

**Spec coverage:** in-app summary (channels/messages/assets/bytes/duration) ✔ (Task 1/3); failures in summary ✔ (`FailedChannels` + suffix); OS unattended cue ✔ (Windows taskbar flash, Task 2); no fragile native-toast dep ✔ (deliberately excluded); extensible payload ✔ (`FailedChannels` field present, populated). 

**Honesty note for the completion report:** native toast was deliberately skipped (unreliable/unverifiable for a portable unsigned exe); delivered in-app summary + Windows taskbar flash as the unattended cue. The user can ask for the toast dependency if they want it anyway.

**Manual smoke (needs token, user-run):** run a multi-channel export; confirm the summary snackbar reads e.g. "Exported 5 channel(s) · 1,234 message(s) · 12 asset(s) · 3.4 MB · 1m20s"; with the window minimized/unfocused, confirm the taskbar button flashes when it finishes; if a channel fails, confirm "· 1 failed" appears.

**Type consistency:** `ChannelExportStats`/`ExportSummary`/`ExportSummarizer.Summarize` (Task 1) used verbatim by the GUI (Task 3); `ExportedFile` (from feature #1) consumed by `SumFileSizes`; `CompletionAttention.FlashIfUnfocused()` matches the call site.
