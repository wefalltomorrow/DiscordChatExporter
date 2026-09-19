using System;
using System.Collections.Concurrent;
using System.Globalization;
using DiscordChatExporter.Core.Exporting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DiscordChatExporter.Cli.Utils;

// Replaces the progress bar with what the export is actually doing. A bar conveys one number that
// the percentage next to it already spells out, while these four say where the time and the
// requests are going.
public class ExportStatsColumn : ProgressColumn
{
    // Spectre's own per-task state bag only accepts value types, so the counters are kept here
    // and looked up by task ID on every refresh
    private readonly ConcurrentDictionary<int, ExportStats> _statsByTaskId = new();

    // Exactly the width of the formatted line below, so that the fields stay put as the numbers
    // grow. Declaring it any smaller makes Spectre wrap the cell and the layout falls apart.
    private const int Width = 56;

    protected override bool NoWrap => true;

    public override int? GetColumnWidth(RenderOptions options) => Width;

    public void Attach(ProgressTask task, ExportStats stats) => _statsByTaskId[task.Id] = stats;

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    // Binary units throughout, stepping up only once the smaller one would read as four digits
    private static string FormatSize(long bytes)
    {
        var kibs = bytes / 1024.0;
        if (kibs < 1000)
            return string.Create(CultureInfo.InvariantCulture, $"{kibs, 6:N1} KiB");

        var mibs = kibs / 1024.0;
        if (mibs < 1000)
            return string.Create(CultureInfo.InvariantCulture, $"{mibs, 6:N1} MiB");

        return string.Create(CultureInfo.InvariantCulture, $"{mibs / 1024.0, 6:N1} GiB");
    }

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        if (!_statsByTaskId.TryGetValue(task.Id, out var stats))
            return new Text(string.Empty);

        // Discord only knows a total for threads, and only an approximate one, so most exports
        // show just what has been written so far
        var messages = stats.TotalMessages is { } total
            ? $"{FormatCount(stats.MessagesExported)}/{FormatCount(total)}"
            : FormatCount(stats.MessagesExported);

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{messages, 13} msg {FormatSize(stats.BytesDownloaded)} in {FormatSize(stats.BytesExported)} out {FormatCount(stats.RequestCount), 5} req"
        );

        // Safety net for numbers larger than the fields were sized for: losing the tail of the
        // line is better than having it wrap and push every other row out of alignment
        return new Text(text.Length > Width ? text[..Width] : text, new Style(Color.Grey));
    }
}
