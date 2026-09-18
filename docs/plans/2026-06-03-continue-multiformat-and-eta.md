# Continue Export: CSV + HTML support, and Export ETA — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the GUI "Continue export" feature (JSON shipped) to also continue **CSV** and **HTML** exports, and add a live **time-remaining ETA** beside the dashboard progress bar.

**Architecture:** Add a shared `ContinuationCutoff` record and a `ContinuationFormat` dispatcher (extension → per-format inspector/merger) under `Core/Exporting/Continuation/`. JSON reuses the existing `JsonExportInspector`/`JsonExportMerger`; new `Csv*` and `Html*` handlers join. `DashboardViewModel.ContinueExportAsync` is generalized to dispatch by extension. HTML uses a hardened string-splice anchored on minifier-stable class tokens, with a validate-before-commit gate. ETA is a pure `EtaEstimator` (rolling window, injected clock) wired to the existing muxed `Progress`.

**Tech Stack:** C# / .NET 10, Avalonia + CommunityToolkit.Mvvm, System.Text.RegularExpressions, WebMarkupMin (already a Core dependency), xUnit + FluentAssertions.

**Assumptions:**
- Channel identity for CSV/HTML comes from the **filename** `[<channelId>]` token (default export naming). Assumes default-named files — will NOT continue a renamed CSV/HTML lacking `[<id>]` (refused).
- HTML cutoff is the exact last `data-message-id`; CSV cutoff is the last row's ISO `"o"` `Date` → `Snowflake.FromDate` (ms precision; boundary rows skipped). Assumes default chronological order — reverse exports refused.
- Continue keeps media OFF (new messages use remote URLs), so no relative-asset-path concerns. Assumes the v1 media-off behavior is unchanged.
- ETA reads the existing timestamp-based progress fraction; it shows time remaining only (no msgs/sec).

---

## File Structure

| Path | Responsibility |
|---|---|
| `Core/Exporting/Continuation/ContinuationCutoff.cs` | Shared record returned by every inspector. |
| `Core/Exporting/Continuation/InvalidExportException.cs` | Non-fatal exception for unreadable/unsupported exports (CSV/HTML/dispatch). |
| `Core/Exporting/Continuation/FileNameChannelId.cs` | Parse `[<id>]` from a filename. |
| `Core/Exporting/Continuation/CsvExportInspector.cs` | CSV cutoff (last-row timestamp). |
| `Core/Exporting/Continuation/CsvExportMerger.cs` | CSV merge (append rows, skip header + boundary rows). |
| `Core/Exporting/Continuation/HtmlExportInspector.cs` | HTML cutoff (last `data-message-id`). |
| `Core/Exporting/Continuation/HtmlExportMerger.cs` | HTML splice + dedupe + count recompute + validate gate. |
| `Core/Exporting/Continuation/ContinuationFormat.cs` | Extension → handler dispatch; `IsSupportedExtension`, `ReadCutoffAsync`, `MergeAsync`. |
| `Core/Exporting/EtaEstimator.cs` | Pure rolling-window ETA estimator. |
| `Cli.Tests/Specs/Continuation/*Specs.cs` + `Infra/HtmlSample.cs` | Tests + HTML fixture helper (real minifier). |
| `Gui/ViewModels/Components/DashboardViewModel.cs` | Generalize `ContinueExportAsync`; add ETA wiring. |
| `Gui/Views/Components/DashboardView.axaml` | ETA `TextBlock`. |
| `Gui/Localization/LocalizationManager*.cs` | New strings. |

**Run tests (token-free):**
`dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation|FullyQualifiedName~Eta"`

All work on branch `prime` in
`C:\Users\ExampleUser\Documents\Codex\2026-06-02\hey-do-you-see-the-discord\work\DiscordChatExporter`
(run dotnet/git from there: PowerShell `Push-Location`/`Pop-Location` or absolute paths). Stage only the files each task names. CSharpier runs on build — match existing style.

---

## Phase 0 — Shared scaffolding

### Task 1: `ContinuationCutoff`, `InvalidExportException`, `FileNameChannelId`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/ContinuationCutoff.cs`
- Create: `DiscordChatExporter.Core/Exporting/Continuation/InvalidExportException.cs`
- Create: `DiscordChatExporter.Core/Exporting/Continuation/FileNameChannelId.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/FileNameChannelIdSpecs.cs`

- [ ] **Step 1: Write the failing test** — `FileNameChannelIdSpecs.cs`:

```csharp
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class FileNameChannelIdSpecs
{
    [Theory]
    [InlineData(@"C:\x\Guild - general [123456789012345678].csv", 123456789012345678UL)]
    [InlineData("Guild - general [42] (after 2026-01-01).html", 42UL)]
    [InlineData("My Server - parent - child [999].json", 999UL)]
    public void I_can_parse_the_channel_id_from_a_default_export_filename(string path, ulong expected)
    {
        FileNameChannelId.TryParse(path).Should().Be(new DiscordChatExporter.Core.Discord.Snowflake(expected));
    }

    [Theory]
    [InlineData(@"C:\x\my-renamed-export.csv")]
    [InlineData("chat.html")]
    [InlineData("notes [abc].txt")] // non-numeric brackets
    public void I_get_null_when_the_filename_has_no_channel_id(string path)
    {
        FileNameChannelId.TryParse(path).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~FileNameChannelIdSpecs"`
Expected: FAIL (does not compile — `FileNameChannelId`/`ContinuationCutoff`/`InvalidExportException` missing).

- [ ] **Step 3: Implement**

`ContinuationCutoff.cs`:
```csharp
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public sealed record ContinuationCutoff(
    Snowflake ChannelId,
    Snowflake Cutoff,        // exact message id (JSON/HTML) or FromDate(lastTimestamp) (CSV)
    Snowflake? Before,
    bool IsChronological,
    long ExistingCount,
    bool CutoffIsExact       // true for JSON/HTML; false for CSV (boundary-skip on merge)
);
```

`InvalidExportException.cs`:
```csharp
using System;
using DiscordChatExporter.Core.Exceptions;

namespace DiscordChatExporter.Core.Exporting.Continuation;

// Non-fatal so the GUI can surface it via `when (!ex.IsFatal)`.
public class InvalidExportException(string message, Exception? innerException = null)
    : DiscordChatExporterException(message, false, innerException);
```

`FileNameChannelId.cs`:
```csharp
using System.IO;
using System.Text.RegularExpressions;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class FileNameChannelId
{
    // Default export names embed the channel id as "[<digits>]" (ExportRequest.GetDefaultOutputFileName).
    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex IdRegex();

    public static Snowflake? TryParse(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var match = IdRegex().Match(name);
        return match.Success ? Snowflake.Parse(match.Groups[1].Value) : null;
    }
}
```
(Confirm `DiscordChatExporterException` ctor is `(string, bool isFatal = false, Exception? = null)` — it is, per the JSON v1 work; the `false` makes this non-fatal.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~FileNameChannelIdSpecs"`
Expected: PASS (6 cases).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/ContinuationCutoff.cs DiscordChatExporter.Core/Exporting/Continuation/InvalidExportException.cs DiscordChatExporter.Core/Exporting/Continuation/FileNameChannelId.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/FileNameChannelIdSpecs.cs
git commit -m "Add ContinuationCutoff, InvalidExportException, and FileNameChannelId for multi-format continue"
```

---

## Phase A — CSV

### Task 2: `CsvExportInspector`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/CsvExportInspector.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/CsvExportInspectorSpecs.cs`

**Background:** the CSV writer's header is `AuthorID,Author,Date,Content,Attachments,Reactions` and the `Date` column is ISO `"o"` (`CsvMessageWriter.cs`). Fields are quoted with doubled inner quotes; Content can contain commas/quotes/newlines, so parse RFC-4180-style, not naive line split. `Date` is column index 2.

- [ ] **Step 1: Write the failing test** — `CsvExportInspectorSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class CsvExportInspectorSpecs
{
    private static async Task<string> WriteAsync(string content, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name} [222] - {Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private const string Header = "AuthorID,Author,Date,Content,Attachments,Reactions\r\n";

    [Fact]
    public async Task I_can_read_the_cutoff_count_and_order_from_a_csv_export()
    {
        var body =
            "\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"hi\",\"\",\"\"\r\n" +
            "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"bye, really\",\"\",\"\"\r\n";
        var path = await WriteAsync(Header + body, "Guild - general");
        try
        {
            var info = await CsvExportInspector.InspectAsync(path);
            info.ChannelId.Value.Should().Be(222UL);
            info.ExistingCount.Should().Be(2);
            info.IsChronological.Should().BeTrue();
            info.CutoffIsExact.Should().BeFalse();
            // cutoff is FromDate(last row date)
            info.Cutoff.ToDate().Should().BeCloseTo(
                new DateTimeOffset(2021, 07, 24, 13, 49, 13, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task I_can_parse_rows_whose_content_contains_commas_quotes_and_newlines()
    {
        var body =
            "\"5\",\"A\",\"2021-07-19T13:34:18.0000000+00:00\",\"line1\nline2, with ""quote""\",\"\",\"\"\r\n" +
            "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"ok\",\"\",\"\"\r\n";
        var path = await WriteAsync(Header + body, "Guild - general");
        try
        {
            var info = await CsvExportInspector.InspectAsync(path);
            info.ExistingCount.Should().Be(2); // the embedded newline did NOT create a phantom row
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task I_cannot_continue_a_csv_with_no_data_rows()
    {
        var path = await WriteAsync(Header, "Guild - general");
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task I_cannot_continue_a_csv_whose_filename_lacks_a_channel_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"renamed-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, Header + "\"5\",\"A\",\"2021-07-24T13:49:13.0000000+00:00\",\"x\",\"\",\"\"\r\n");
        try
        {
            var act = async () => await CsvExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~CsvExportInspectorSpecs"`
Expected: FAIL (compile — `CsvExportInspector` missing).

- [ ] **Step 3: Implement** — `CsvExportInspector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class CsvExportInspector
{
    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        var channelId =
            FileNameChannelId.TryParse(filePath)
            ?? throw new InvalidExportException(
                "Could not determine the channel for this CSV export. "
                    + "Keep the default file name (it includes the channel id) or re-export."
            );

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        var rows = ParseCsv(text);
        // rows[0] is the header
        if (rows.Count <= 1)
            throw new InvalidExportException("The CSV export contains no messages to continue from.");

        DateTimeOffset? firstDate = null;
        DateTimeOffset? lastDate = null;
        long count = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var fields = rows[i];
            if (fields.Count < 3)
                continue;
            if (
                DateTimeOffset.TryParse(
                    fields[2],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var date
                )
            )
            {
                firstDate ??= date;
                lastDate = date;
            }
            count++;
        }

        if (lastDate is null)
            throw new InvalidExportException("The CSV export has no parseable message dates.");

        var isChronological = firstDate is null || firstDate <= lastDate;
        return new ContinuationCutoff(
            channelId,
            Snowflake.FromDate(lastDate.Value),
            null, // CSV has no recorded 'before' bound
            isChronological,
            count,
            CutoffIsExact: false
        );
    }

    // Minimal RFC-4180 reader: rows of fields; quotes doubled; commas/CR/LF allowed inside quotes.
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r':
                    i++;
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }
        // trailing field/row (no final newline)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~CsvExportInspectorSpecs"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/CsvExportInspector.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/CsvExportInspectorSpecs.cs
git commit -m "Add CsvExportInspector (timestamp cutoff from last CSV row)"
```

---

### Task 3: `CsvExportMerger`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/CsvExportMerger.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/CsvExportMergerSpecs.cs`

- [ ] **Step 1: Write the failing test** — `CsvExportMergerSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class CsvExportMergerSpecs
{
    private const string Header = "AuthorID,Author,Date,Content,Attachments,Reactions\r\n";
    private static string Row(string date, string content) =>
        $"\"5\",\"A\",\"{date}\",\"{content}\",\"\",\"\"\r\n";

    private static async Task<string> WriteAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-csvmerge-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task It_appends_new_rows_skipping_the_header_and_boundary_rows()
    {
        var d1 = "2021-07-19T13:34:18.0000000+00:00";
        var d2 = "2021-07-24T13:49:13.0000000+00:00";
        var d3 = "2021-07-25T10:00:00.0000000+00:00";
        var existing = await WriteAsync(Header + Row(d1, "a") + Row(d2, "b"));
        // temp re-export starts AT the cutoff (d2) due to ms-truncated 'after' — d2 must be skipped.
        var fresh = await WriteAsync(Header + Row(d2, "b") + Row(d3, "c"));
        var cutoff = new ContinuationCutoff(
            new Snowflake(222), Snowflake.FromDate(DateTimeOffset.Parse(d2)), null, true, 2, false);
        try
        {
            var added = await CsvExportMerger.MergeAsync(existing, fresh, cutoff);
            added.Should().Be(1); // only d3 appended

            var lines = (await File.ReadAllLinesAsync(existing)).Where(l => l.Length > 0).ToArray();
            lines[0].Should().StartWith("AuthorID,");        // header preserved, once
            lines.Count(l => l.StartsWith("AuthorID,")).Should().Be(1);
            lines.Last().Should().Contain("2021-07-25");      // new row present
            File.Exists(existing + ".bak").Should().BeTrue();
        }
        finally
        {
            File.Delete(existing); File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~CsvExportMergerSpecs"`
Expected: FAIL (compile — `CsvExportMerger` missing).

- [ ] **Step 3: Implement** — `CsvExportMerger.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class CsvExportMerger
{
    // Appends the new export's data rows (minus its header) to the existing CSV, skipping rows at or
    // before the cutoff timestamp. Writes via temp + atomic File.Replace + .bak. Returns rows added.
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newRowsFilePath,
        ContinuationCutoff cutoff,
        CancellationToken cancellationToken = default
    )
    {
        var newText = await File.ReadAllBytesAsync(newRowsFilePath, cancellationToken)
            .ContinueWith(t => System.Text.Encoding.UTF8.GetString(t.Result), cancellationToken);
        var newRows = CsvExportInspector.ParseCsv(newText);

        var tempPath = existingFilePath + ".merging.tmp";
        long added = 0;
        try
        {
            // Copy the existing file verbatim, then append qualifying new rows.
            File.Copy(existingFilePath, tempPath, true);
            await using (var writer = new StreamWriter(new FileStream(tempPath, FileMode.Append)))
            {
                for (var i = 1; i < newRows.Count; i++) // skip header row 0
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = newRows[i];
                    if (fields.Count < 3)
                        continue;
                    if (
                        DateTimeOffset.TryParse(
                            fields[2],
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var date
                        )
                        && Snowflake.FromDate(date).Value <= cutoff.Cutoff.Value
                    )
                        continue; // boundary / already-present row

                    await writer.WriteAsync(EncodeRow(fields));
                    added++;
                }
            }
            File.Replace(tempPath, existingFilePath, existingFilePath + ".bak");
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
        return added;
    }

    private static string EncodeRow(System.Collections.Generic.IReadOnlyList<string> fields)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"').Append(fields[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        sb.Append("\r\n");
        return sb.ToString();
    }
}
```
Note: re-encoding round-trips through the parser, so embedded commas/quotes/newlines are re-quoted correctly (matches `CsvMessageWriter.CsvEncode`).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~CsvExportMergerSpecs"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/CsvExportMerger.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/CsvExportMergerSpecs.cs
git commit -m "Add CsvExportMerger (append rows past the cutoff timestamp)"
```

---

## Phase B — HTML

### Task 4: HTML fixture helper using the real minifier (pre-implementation requirement)

**Files:**
- Modify: `DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj` (add `WebMarkupMin.Core` reference)
- Create: `DiscordChatExporter.Cli.Tests/Infra/HtmlSample.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlSampleSpecs.cs`

**Why:** the spec forbids hand-guessing the minified structure. `HtmlSample` builds fixtures by running the SAME minifier `HtmlMessageWriter` uses (`new HtmlMinifier()`), minifying each block separately and joining with `\n` — exactly mirroring `HtmlMessageWriter` (which does `WriteLineAsync(Minify(block))` per block). Block markup is copied from the real templates (`PreambleTemplate.cshtml` chatlog open, `MessageGroupTemplate.cshtml:48` message container, `PostambleTemplate.cshtml` close + count). The `HtmlSampleSpecs` test asserts the minifier's real behavior so later tasks build on verified bytes.

- [ ] **Step 1: Add the package reference.** In `DiscordChatExporter.Cli.Tests.csproj`, inside the main `<ItemGroup>` of `<PackageReference>`s, add:
```xml
    <PackageReference Include="WebMarkupMin.Core" />
```
(Version is centrally managed by `Directory.Packages.props`, which already pins it for Core.)

- [ ] **Step 2: Write the helper + its sanity test**

`Infra/HtmlSample.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using WebMarkupMin.Core;

namespace DiscordChatExporter.Cli.Tests.Infra;

// Produces HTML byte-shaped like a real DiscordChatExporter HTML export: each block minified
// separately with the SAME minifier HtmlMessageWriter uses, joined by newlines.
public static class HtmlSample
{
    private static readonly HtmlMinifier Minifier = new();

    private static string Minify(string html) => Minifier.Minify(html, false).MinifiedContent;

    // A minimal preamble that opens the chatlog container (mirrors PreambleTemplate's tail).
    private const string PreambleHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><div class=\"preamble\">"
        + "<div class=\"preamble__entry\">Guild</div></div><div class=\"chatlog\">";

    private static string MessageGroupHtml(IEnumerable<long> ids) =>
        "<div class=\"chatlog__message-group\">"
        + string.Concat(
            ids.Select(id =>
                $"<div id=\"chatlog__message-container-{id}\" class=\"chatlog__message-container\" data-message-id=\"{id}\">"
                + "<div class=\"chatlog__content chatlog__markdown\"><span class=\"chatlog__markdown-preserve\">msg "
                + id
                + "</span></div></div>"
            )
        )
        + "</div>";

    private static string PostambleHtml(long count) =>
        "</div><div class=\"postamble\"><div class=\"postamble__entry\">Exported "
        + count.ToString("n0")
        + " message(s)</div></div></body></html>";

    // Build a full export. groups = list of message-id groups (each inner list is one author group).
    public static string Export(params long[][] groups)
    {
        var blocks = new List<string> { Minify(PreambleHtml) };
        blocks.AddRange(groups.Select(g => Minify(MessageGroupHtml(g))));
        var total = groups.Sum(g => g.LongLength);
        blocks.Add(Minify(PostambleHtml(total)));
        return string.Join("\n", blocks);
    }
}
```

`Specs/Continuation/HtmlSampleSpecs.cs` (locks in the REAL minifier behavior the merge depends on):
```csharp
using DiscordChatExporter.Cli.Tests.Infra;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlSampleSpecs
{
    [Fact]
    public void The_minifier_strips_quotes_on_single_token_values_but_keeps_class_tokens_present()
    {
        var html = HtmlSample.Export([1000L, 2000L]);
        // data-message-id values are single-token => quotes dropped by Html5 mode:
        html.Should().Contain("data-message-id=1000").And.Contain("data-message-id=2000");
        // the splice anchors survive as literal tokens (quoted on the chatlog open):
        html.Should().Contain("<div class=\"chatlog\">");
        html.Should().MatchRegex("<div class=\"?postamble\"?>");
        html.Should().Contain("Exported 2 message(s)");
    }
}
```
(If the real minifier keeps quotes on `data-message-id` in this environment, ADJUST the assertion to whatever it actually emits — the point is to record the REAL behavior; later regexes are quote-tolerant either way.)

- [ ] **Step 3: Run to verify** (this is characterization, not red→green)

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~HtmlSampleSpecs"`
Expected: PASS. If an assertion is wrong vs the real minifier output, fix the assertion to match reality and note the observed bytes in a comment.

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj DiscordChatExporter.Cli.Tests/Infra/HtmlSample.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlSampleSpecs.cs
git commit -m "Add HtmlSample fixture helper using the real WebMarkupMin minifier"
```

---

### Task 5: `HtmlExportInspector`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/HtmlExportInspector.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlExportInspectorSpecs.cs`

- [ ] **Step 1: Write the failing test** — `HtmlExportInspectorSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportInspectorSpecs
{
    private static async Task<string> WriteAsync(string html)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Guild - general [222] - {Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    [Fact]
    public async Task I_can_read_the_exact_cutoff_count_and_channel_from_an_html_export()
    {
        var path = await WriteAsync(HtmlSample.Export([1000L, 2000L], [3000L]));
        try
        {
            var info = await HtmlExportInspector.InspectAsync(path);
            info.ChannelId.Value.Should().Be(222UL);
            info.Cutoff.Value.Should().Be(3000UL);
            info.CutoffIsExact.Should().BeTrue();
            info.ExistingCount.Should().Be(3);
            info.IsChronological.Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task I_cannot_continue_an_html_export_with_no_messages()
    {
        var path = await WriteAsync(HtmlSample.Export());
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~HtmlExportInspectorSpecs"`
Expected: FAIL (compile — `HtmlExportInspector` missing).

- [ ] **Step 3: Implement** — `HtmlExportInspector.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class HtmlExportInspector
{
    [GeneratedRegex("data-message-id=\"?(\\d+)\"?")]
    internal static partial Regex MessageIdRegex();

    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        var channelId =
            FileNameChannelId.TryParse(filePath)
            ?? throw new InvalidExportException(
                "Could not determine the channel for this HTML export. "
                    + "Keep the default file name (it includes the channel id) or re-export."
            );

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        var ids = MessageIdRegex()
            .Matches(text)
            .Select(m => Snowflake.Parse(m.Groups[1].Value))
            .ToArray();

        if (ids.Length == 0)
            throw new InvalidExportException("The HTML export contains no messages to continue from.");

        var first = ids[0];
        var last = ids[^1];
        return new ContinuationCutoff(
            channelId,
            last,
            null,
            IsChronological: first.Value <= last.Value,
            ExistingCount: ids.Length,
            CutoffIsExact: true
        );
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~HtmlExportInspectorSpecs"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/HtmlExportInspector.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlExportInspectorSpecs.cs
git commit -m "Add HtmlExportInspector (exact cutoff from last data-message-id)"
```

---

### Task 6: `HtmlExportMerger` (hardened splice + validate gate)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/HtmlExportMerger.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlExportMergerSpecs.cs`

- [ ] **Step 1: Write the failing test** — `HtmlExportMergerSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportMergerSpecs
{
    private static async Task<string> WriteAsync(string html, string suffix = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-htmlmerge-{Guid.NewGuid():N}{suffix}.html");
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    private static long[] Ids(string html) =>
        HtmlExportInspector.MessageIdRegex().Matches(html).Select(m => long.Parse(m.Groups[1].Value)).ToArray();

    [Fact]
    public async Task It_splices_new_groups_recomputes_the_count_and_dedupes_overlap()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L, 2000L]));
        // temp re-export overlaps on 2000 (overlap window) and adds 3000:
        var fresh = await WriteAsync(HtmlSample.Export([2000L, 3000L]), "-new");
        var cutoff = new ContinuationCutoff(new Snowflake(222), new Snowflake(2000), null, true, 2, true);
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(3);

            var merged = await File.ReadAllTextAsync(existing);
            Ids(merged).Should().Equal(1000L, 2000L, 3000L); // ascending, deduped (2000 not doubled)
            Regex.Matches(merged, "<div class=\"chatlog\">").Count.Should().Be(1);
            Regex.Matches(merged, "<div class=\"?postamble\"?>").Count.Should().Be(1);
            merged.Should().Contain("Exported 3 message(s)");
            File.Exists(existing + ".bak").Should().BeTrue();
        }
        finally
        {
            File.Delete(existing); File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_appends_into_an_html_export_that_had_no_messages()
    {
        var existing = await WriteAsync(HtmlSample.Export());
        var fresh = await WriteAsync(HtmlSample.Export([1000L]), "-new");
        var cutoff = new ContinuationCutoff(new Snowflake(222), new Snowflake(1), null, true, 0, true);
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(1);
            Ids(await File.ReadAllTextAsync(existing)).Should().Equal(1000L);
        }
        finally
        {
            File.Delete(existing); File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~HtmlExportMergerSpecs"`
Expected: FAIL (compile — `HtmlExportMerger` missing).

- [ ] **Step 3: Implement** — `HtmlExportMerger.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class HtmlExportMerger
{
    [GeneratedRegex("<div class=\"?postamble\"?>")]
    private static partial Regex PostambleOpenRegex();

    [GeneratedRegex("<div class=\"chatlog\">")]
    private static partial Regex ChatlogOpenRegex();

    [GeneratedRegex("(Exported )(.+?)( message\\(s\\))")]
    private static partial Regex CountRegex();

    private const string ChatlogOpen = "<div class=\"chatlog\">";

    // Splices the new export's message groups into the existing HTML before the postamble, dedupes
    // by data-message-id, recomputes the "Exported N" count, validates, then atomic-replaces. Returns
    // the merged total message count. Throws InvalidExportException if the merged result fails validation.
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        ContinuationCutoff cutoff,
        CancellationToken cancellationToken = default
    )
    {
        var oldHtml = await File.ReadAllTextAsync(existingFilePath, cancellationToken);
        var newHtml = await File.ReadAllTextAsync(newMessagesFilePath, cancellationToken);

        var oldIds = HtmlExportInspector.MessageIdRegex().Matches(oldHtml).Select(m => m.Groups[1].Value).ToHashSet();

        // 1) Slice the new groups out of the temp file (between chatlog-open and the postamble-preceding </div>).
        var newSlice = ExtractGroups(newHtml);

        // 2) Drop any new group whose first data-message-id is already present (dedupe overlap).
        newSlice = DedupeGroups(newSlice, oldIds);

        // 3) Find the splice point in the OLD file: the </div> right before the postamble open.
        var spliceAt = FindChatlogCloseBeforePostamble(oldHtml);

        var merged = oldHtml[..spliceAt] + newSlice + oldHtml[spliceAt..];

        // 4) Recompute the count from the merged data-message-id total and rewrite the postamble entry.
        var totalCount = HtmlExportInspector.MessageIdRegex().Matches(merged).Count;
        merged = RewriteCount(merged, totalCount);

        // 5) Validate before committing.
        Validate(merged, oldHtml);

        var tempPath = existingFilePath + ".merging.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, merged, cancellationToken);
            File.Replace(tempPath, existingFilePath, existingFilePath + ".bak");
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
        return totalCount;
    }

    // Index of the chatlog-closing </div> immediately preceding the (last) postamble open.
    private static int FindChatlogCloseBeforePostamble(string html)
    {
        var post = PostambleOpenRegex().Matches(html).LastOrDefault();
        if (post is null)
            throw new InvalidExportException("Not a recognizable HTML export (no postamble).");
        var close = html.LastIndexOf("</div>", post.Index, StringComparison.Ordinal);
        if (close < 0)
            throw new InvalidExportException("Could not locate the chatlog boundary in the HTML export.");
        return close;
    }

    // The message-group markup between the chatlog open and the postamble-preceding </div>.
    private static string ExtractGroups(string html)
    {
        var openMatch = ChatlogOpenRegex().Match(html);
        if (!openMatch.Success)
            return "";
        var start = openMatch.Index + openMatch.Length;
        var end = FindChatlogCloseBeforePostamble(html);
        return end > start ? html[start..end] : "";
    }

    private static string DedupeGroups(string slice, HashSet<string> existingIds)
    {
        if (string.IsNullOrEmpty(slice))
            return slice;
        // Split into top-level message-group divs and keep a group only if none of its ids are already present.
        var groups = SplitMessageGroups(slice);
        var kept = groups.Where(g =>
            HtmlExportInspector.MessageIdRegex().Matches(g).All(m => !existingIds.Contains(m.Groups[1].Value)));
        return string.Concat(kept);
    }

    private static IEnumerable<string> SplitMessageGroups(string slice)
    {
        const string marker = "<div class=\"chatlog__message-group\">";
        var indices = new List<int>();
        for (var i = slice.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = slice.IndexOf(marker, i + 1, StringComparison.Ordinal))
            indices.Add(i);
        if (indices.Count == 0)
        {
            yield return slice;
            yield break;
        }
        for (var k = 0; k < indices.Count; k++)
        {
            var start = indices[k];
            var end = k + 1 < indices.Count ? indices[k + 1] : slice.Length;
            yield return slice[start..end];
        }
    }

    private static string RewriteCount(string html, long total)
    {
        var match = CountRegex().Match(html);
        if (!match.Success)
            return html; // leave unchanged rather than corrupt (cosmetic)
        var replacement = match.Groups[1].Value + total.ToString("n0") + match.Groups[3].Value;
        return html[..match.Index] + replacement + html[(match.Index + match.Length)..];
    }

    private static void Validate(string merged, string oldHtml)
    {
        if (ChatlogOpenRegex().Matches(merged).Count != 1)
            throw new InvalidExportException("HTML merge produced an invalid file (chatlog container count).");
        if (PostambleOpenRegex().Matches(merged).Count != 1)
            throw new InvalidExportException("HTML merge produced an invalid file (postamble count).");
        var ids = HtmlExportInspector.MessageIdRegex().Matches(merged).Select(m => ulong.Parse(m.Groups[1].Value)).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidExportException("HTML merge produced duplicate message ids.");
        for (var i = 1; i < ids.Length; i++)
            if (ids[i] < ids[i - 1])
                throw new InvalidExportException("HTML merge produced out-of-order message ids.");
        if (merged.Length <= oldHtml.Length)
            throw new InvalidExportException("HTML merge did not add any content.");
    }
}
```
Note: `RewriteCount` matches `Exported … message(s)` on the culture-invariant English wrapper; the `.ToString("n0")` here uses the invariant culture (fixtures use invariant). In production the existing file's number is locale-grouped; replacing the inner digits with an invariant `n0` is acceptable (cosmetic). If exact locale parity is later required, thread the export `CultureInfo` into `MergeAsync`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~HtmlExportMergerSpecs"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/HtmlExportMerger.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/HtmlExportMergerSpecs.cs
git commit -m "Add HtmlExportMerger (hardened splice, dedupe, count recompute, validate gate)"
```

---

## Phase C — Dispatcher + GUI wiring

### Task 7: `ContinuationFormat` dispatcher

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/ContinuationFormat.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/ContinuationFormatSpecs.cs`

**Note:** JSON's existing `JsonExportInspector.InspectAsync` returns `JsonExportInfo` (GuildId, ChannelId, Before, LastMessageId, MessageCount, IsChronological), and `JsonExportMerger.MergeAsync(existing, newPath, exportedAt, ct)`. The dispatcher maps JSON into the shared shape.

- [ ] **Step 1: Write the failing test** — `ContinuationFormatSpecs.cs`:

```csharp
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class ContinuationFormatSpecs
{
    [Theory]
    [InlineData("x.json", true)]
    [InlineData("x.html", true)]
    [InlineData("x.htm", true)]
    [InlineData("x.csv", true)]
    [InlineData("x.txt", false)]
    [InlineData("x.xml", false)]
    public void It_recognizes_supported_extensions(string path, bool supported)
    {
        ContinuationFormat.IsSupportedExtension(path).Should().Be(supported);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ContinuationFormatSpecs"`
Expected: FAIL (compile — `ContinuationFormat` missing).

- [ ] **Step 3: Implement** — `ContinuationFormat.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class ContinuationFormat
{
    public static bool IsSupportedExtension(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() is ".json" or ".html" or ".htm" or ".csv";

    // The ExportFormat to use for the temp re-export, matching the existing file.
    public static ExportFormat FormatFor(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => ExportFormat.Json,
            ".html" or ".htm" => ExportFormat.HtmlDark,
            ".csv" => ExportFormat.Csv,
            var ext => throw new InvalidExportException($"Continuing {ext} exports is not supported."),
        };

    public static async ValueTask<ContinuationCutoff> ReadCutoffAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => await ReadJsonAsync(filePath, cancellationToken),
            ".html" or ".htm" => await HtmlExportInspector.InspectAsync(filePath, cancellationToken),
            ".csv" => await CsvExportInspector.InspectAsync(filePath, cancellationToken),
            var ext => throw new InvalidExportException($"Continuing {ext} exports is not supported."),
        };

    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    ) =>
        Path.GetExtension(existingFilePath).ToLowerInvariant() switch
        {
            ".json" => await JsonExportMerger.MergeAsync(existingFilePath, newMessagesFilePath, exportedAt, cancellationToken),
            ".html" or ".htm" => await HtmlExportMerger.MergeAsync(existingFilePath, newMessagesFilePath, cutoff, cancellationToken),
            ".csv" => await CsvExportMerger.MergeAsync(existingFilePath, newMessagesFilePath, cutoff, cancellationToken),
            var ext => throw new InvalidExportException($"Continuing {ext} exports is not supported."),
        };

    private static async ValueTask<ContinuationCutoff> ReadJsonAsync(string filePath, CancellationToken ct)
    {
        var info = await JsonExportInspector.InspectAsync(filePath, ct);
        var channelId = FileNameChannelId.TryParse(filePath) ?? info.ChannelId;
        return new ContinuationCutoff(
            channelId,
            info.LastMessageId,
            info.Before,
            info.IsChronological,
            info.MessageCount,
            CutoffIsExact: true
        );
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ContinuationFormatSpecs"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/ContinuationFormat.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/ContinuationFormatSpecs.cs
git commit -m "Add ContinuationFormat dispatcher (extension to per-format handler)"
```

---

### Task 8: Generalize `DashboardViewModel.ContinueExportAsync` + localization

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

**Security flag:** `security` (file IO on a user-chosen path; overwrites the file via the mergers).

- [ ] **Step 1: Add localization strings.** In `LocalizationManager.cs` Dashboard group add:
```csharp
    public string ContinueExportFormatUnsupportedMessage => Get();
    public string ContinueExportChannelUnknownMessage => Get();
```
In `LocalizationManager.English.cs` (matching the `nameof(...)` dict style used there) add:
```csharp
        [nameof(LocalizationManager.ContinueExportFormatUnsupportedMessage)] =
            "Continuing this file type is not supported (JSON, HTML, and CSV are).",
        [nameof(LocalizationManager.ContinueExportChannelUnknownMessage)] =
            "Could not determine the channel from the file. Keep the default file name or re-export.",
```

- [ ] **Step 2: Generalize the command.** In `DashboardViewModel.cs`:

(a) Add usings (only if missing): `using DiscordChatExporter.Core.Exporting.Continuation;` (already imports `...Exporting`, `Avalonia.Platform.Storage`, etc.).

(b) Widen the file picker filter — replace the existing JSON-only `FilePickerFileType` in `ContinueExportAsync` with:
```csharp
        var filePath = await _dialogManager.PromptSingleFilePathAsync(
            [
                new FilePickerFileType("Supported exports (JSON, HTML, CSV)")
                {
                    Patterns = ["*.json", "*.html", "*.htm", "*.csv"],
                },
            ]
        );
```

(c) After the picker null-check and the partitioned-export check, add the unsupported-format guard:
```csharp
        if (!ContinuationFormat.IsSupportedExtension(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportFormatUnsupportedMessage.TrimEnd('.')
            );
            return;
        }
```

(d) Replace the JSON-specific inspect/export/merge body with the format-dispatched version. The cutoff/channel now come from `ContinuationFormat`; the temp export format matches the file; the merge dispatches by extension:
```csharp
            var cutoff = await ContinuationFormat.ReadCutoffAsync(filePath);

            if (!cutoff.IsChronological)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportReverseUnsupportedMessage.TrimEnd('.')
                );
                return;
            }

            var channel = await _discord.GetChannelAsync(cutoff.ChannelId);
            var guild = channel.IsDirect
                ? Guild.DirectMessages
                : await _discord.GetGuildAsync(channel.GuildId);

            var request = new ExportRequest(
                guild,
                channel,
                tempPath,
                null,
                ContinuationFormat.FormatFor(filePath),
                cutoff.Cutoff,
                cutoff.Before,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                _settingsService.LastShouldFormatMarkdown,
                false,
                false,
                _settingsService.Locale,
                _settingsService.IsUtcNormalizationEnabled
            );

            var exporter = new ChannelExporter(_discord);
            try
            {
                await exporter.ExportChannelAsync(request, progress);
            }
            catch (ChannelEmptyException)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
                return;
            }

            var countBefore = cutoff.ExistingCount;
            var total = await ContinuationFormat.MergeAsync(
                filePath,
                tempPath,
                cutoff,
                DateTimeOffset.Now
            );
            var newMessages = total - countBefore;

            if (newMessages <= 0)
            {
                _snackbarManager.Notify(LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.'));
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ContinueExportSuccessMessage, newMessages)
                );
            }
```
Keep the existing `catch (DiscordChatExporterException ex) when (!ex.IsFatal)` (now also catches `InvalidExportException`, e.g. unknown channel → shows `ex.Message`), the generic catch, and the `finally` (progress completion, IsBusy reset, temp delete). The temp path can keep the `.json` suffix or use a neutral one; the `ExportRequest` format drives the actual content. To avoid a misleading extension, change the temp path to match: `tempPath = Path.Combine(Path.GetTempPath(), $"{Program.Name}-continue-{Guid.NewGuid():N}{Path.GetExtension(filePath)}");`.

- [ ] **Step 3: Verify build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Generalize ContinueExport to dispatch JSON/HTML/CSV by file type"
```

---

## Phase D — ETA

### Task 9: `EtaEstimator`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/EtaEstimator.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/EtaEstimatorSpecs.cs`

- [ ] **Step 1: Write the failing test** — `EtaEstimatorSpecs.cs`:

```csharp
using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class EtaEstimatorSpecs
{
    [Fact]
    public void It_returns_null_until_it_has_a_confident_window()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator();
        eta.Report(0.0, t);
        eta.Estimate.Should().BeNull(); // single sample, no rate yet
    }

    [Fact]
    public void It_estimates_time_remaining_from_a_steady_rate()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.0, t0);
        eta.Report(0.10, t0 + TimeSpan.FromSeconds(5)); // 10% in 5s => 50s total => ~45s left
        eta.Estimate.Should().NotBeNull();
        eta.Estimate!.Value.TotalSeconds.Should().BeApproximately(45, 2);
    }

    [Fact]
    public void It_returns_zero_when_complete()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.0, t0);
        eta.Report(1.0, t0 + TimeSpan.FromSeconds(5));
        eta.Estimate.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void It_returns_null_when_progress_stalls()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.3, t0);
        eta.Report(0.3, t0 + TimeSpan.FromSeconds(5)); // no movement => rate 0 => null
        eta.Estimate.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~EtaEstimatorSpecs"`
Expected: FAIL (compile — `EtaEstimator` missing).

- [ ] **Step 3: Implement** — `EtaEstimator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

// Estimates time remaining from a stream of (fraction, time) samples using a rolling window.
// Pure and clock-injectable for testability. Not thread-safe; call from one thread (the UI).
public sealed class EtaEstimator(TimeSpan? window = null, TimeSpan? minWindow = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(60);
    private readonly TimeSpan _minWindow = minWindow ?? TimeSpan.FromSeconds(3);
    private readonly List<(double Fraction, DateTimeOffset Time)> _samples = [];

    public void Report(double fraction, DateTimeOffset now)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _samples.Add((fraction, now));
        // Drop samples older than the window (keep at least 2).
        var cutoff = now - _window;
        while (_samples.Count > 2 && _samples[0].Time < cutoff)
            _samples.RemoveAt(0);
    }

    public TimeSpan? Estimate
    {
        get
        {
            if (_samples.Count == 0)
                return null;
            var newest = _samples[^1];
            if (newest.Fraction >= 1.0)
                return TimeSpan.Zero;
            var oldest = _samples[0];
            var dt = (newest.Time - oldest.Time).TotalSeconds;
            var df = newest.Fraction - oldest.Fraction;
            if (dt < _minWindow.TotalSeconds || df <= 0)
                return null; // not enough data, or stalled/going backwards
            var rate = df / dt; // fraction per second
            var remaining = (1.0 - newest.Fraction) / rate;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
    }

    public void Reset() => _samples.Clear();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~EtaEstimatorSpecs"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/EtaEstimator.cs DiscordChatExporter.Cli.Tests/Specs/EtaEstimatorSpecs.cs
git commit -m "Add EtaEstimator (rolling-window time-remaining)"
```

---

### Task 10: Wire ETA into the dashboard

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

- [ ] **Step 1: Localization.** Add to `LocalizationManager.cs`:
```csharp
    public string EtaEstimatingText => Get();
    public string EtaRemainingFormat => Get();
```
Add to `LocalizationManager.English.cs`:
```csharp
        [nameof(LocalizationManager.EtaEstimatingText)] = "estimating time remaining…",
        [nameof(LocalizationManager.EtaRemainingFormat)] = "~{0} left",
```

- [ ] **Step 2: ViewModel wiring.** In `DashboardViewModel.cs`:

(a) Add usings if missing: `using System;`, `using DiscordChatExporter.Core.Exporting;` (likely present).

(b) Add a field + observable property + helper. Near the other fields:
```csharp
    private readonly EtaEstimator _etaEstimator = new();
```
Add an observable property (CommunityToolkit source-gen style, matching `IsBusy`):
```csharp
    [ObservableProperty]
    public partial string? EtaText { get; set; }
```

(c) In the constructor, the VM already subscribes to `Progress.WatchProperty(o => o.Current, ...)` (it updates `IsProgressIndeterminate`). Extend that handler to also update the ETA. Replace the existing `Progress.WatchProperty(o => o.Current, _ => OnPropertyChanged(nameof(IsProgressIndeterminate)))` subscription body with one that also calls an `UpdateEta()`:
```csharp
            Progress.WatchProperty(
                o => o.Current,
                _ =>
                {
                    OnPropertyChanged(nameof(IsProgressIndeterminate));
                    UpdateEta();
                }
            ),
```

(d) Add the `UpdateEta` method and reset hooks:
```csharp
    private void UpdateEta()
    {
        if (!IsBusy)
        {
            EtaText = null;
            return;
        }
        _etaEstimator.Report(Progress.Current.Fraction, DateTimeOffset.Now);
        var estimate = _etaEstimator.Estimate;
        EtaText =
            estimate is null
                ? LocalizationManager.EtaEstimatingText
                : estimate.Value <= TimeSpan.Zero
                    ? null
                    : string.Format(LocalizationManager.EtaRemainingFormat, FormatDuration(estimate.Value));
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:D2}s"
        : $"{t.Seconds}s";
```

(e) Reset the estimator and clear text when a run starts/ends. In BOTH `ExportAsync` and `ContinueExportAsync`, right where `IsBusy = true;` is set, add `_etaEstimator.Reset();`. In each `finally` where `IsBusy = false;`, add `EtaText = null;`.

- [ ] **Step 3: View.** In `DashboardView.axaml`, the progress bar is in the header `StackPanel` (the `<ProgressBar Height="2" .../>`). Add an ETA label directly beneath it (still inside that top `StackPanel`):
```xml
            <TextBlock
                Margin="12,0,12,4"
                HorizontalAlignment="Right"
                FontSize="11"
                Foreground="{DynamicResource MaterialDarkForegroundBrush}"
                IsVisible="{Binding !!EtaText}"
                Text="{Binding EtaText}" />
```

- [ ] **Step 4: Verify build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: Build succeeded, 0 errors. (Compiled bindings validate `EtaText` against the `DashboardViewModel` DataType.)

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Views/Components/DashboardView.axaml DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Show estimated time remaining beside the dashboard progress bar"
```

---

## Phase E — Build & deploy

### Task 11: Full test, publish, redeploy

**Files:** none (artifact only)

- [ ] **Step 1: Run the full Continuation + ETA suite**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation|FullyQualifiedName~Eta"`
Expected: PASS (all — the JSON v1 tests, the new CSV/HTML/dispatcher/filename tests, and the ETA tests).

- [ ] **Step 2: Publish the self-contained win-x64 GUI**

Run: `dotnet publish DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release -r win-x64`
Expected: Build succeeded (the Material.Avalonia IL2035/IL2104 trim warnings are pre-existing). Output at `DiscordChatExporter.Gui/bin/Release/net10.0/win-x64/publish/DiscordChatExporter.exe`.

- [ ] **Step 3: Redeploy over the user copy** (after closing any running instance)

Run (PowerShell): `robocopy "DiscordChatExporter.Gui\bin\Release\net10.0\win-x64\publish" "..\..\outputs\DiscordChatExporter-user-copy" /E /NFL /NDL /NJH /NP /R:2 /W:2` (robocopy exit codes 0–7 = success).

- [ ] **Step 4: Manual smoke test (operator).** Launch the exe; continue a CSV, an HTML, and a JSON export; confirm new messages are appended, counts update (HTML "Exported N"), `.bak` files appear, "already up to date" shows when nothing is new, and the ETA text appears during a longer export. (Cannot be automated — desktop GUI.)

- [ ] **Step 5: Commit docs**

```bash
git add docs/plans/2026-06-03-continue-multiformat-and-eta.md docs/specs/2026-06-03-continue-multiformat-and-eta-design.md
git commit -m "Add multi-format continue + ETA plan and design docs"
```

---

## Self-Review

**Spec coverage:**
- CSV continue (cutoff + merge + boundary skip) → Tasks 2, 3. ✓
- HTML continue (exact cutoff, hardened splice, dedupe, count recompute, validate gate) → Tasks 5, 6 (fixtures from real minifier in Task 4). ✓
- Channel id from filename → Task 1 (`FileNameChannelId`), used by CSV/HTML inspectors + JSON dispatch. ✓
- Dispatcher + `.txt`/unknown-channel refusal, format-matched temp export → Tasks 7, 8. ✓
- Reverse/partitioned refusal preserved → Task 8 (reverse via `IsChronological`; partition check retained from v1). ✓
- ETA (rolling window, injected clock, all exports + continue, time-only) → Tasks 9, 10. ✓
- Rebuild + redeploy → Task 11. ✓
- "Capture a real minified sample, don't hand-guess" → Task 4 (real `new HtmlMinifier()`). ✓

**Placeholder scan:** No TBD/TODO; every code step has complete code. The one conditional ("if the real minifier keeps quotes, adjust the assertion") is a deliberate characterization instruction with a concrete action, not a placeholder.

**Type consistency:** `ContinuationCutoff(ChannelId, Cutoff, Before, IsChronological, ExistingCount, CutoffIsExact)` is produced by all inspectors and consumed by `ContinuationFormat`/the mergers/`DashboardViewModel` with matching names. `HtmlExportInspector.MessageIdRegex()` is `internal` and reused by `HtmlExportMerger` + tests. `ContinuationFormat.{IsSupportedExtension,FormatFor,ReadCutoffAsync,MergeAsync}` signatures match their call sites in Task 8. `EtaEstimator.{Report,Estimate,Reset}` match Task 10. `ExportRequest` 15-arg order matches the JSON v1 usage.

**Scope-reduction scan:** "TXT dropped", "media off", "time-only ETA", "accepted cosmetic seam" are all user-approved spec decisions, not silent downgrades.

---

## Execution Handoff

Selection: 11 tasks, context-heavy, independent Core units → **Subagent-Driven**.
