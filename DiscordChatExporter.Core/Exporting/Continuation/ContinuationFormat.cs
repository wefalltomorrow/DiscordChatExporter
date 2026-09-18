using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class ContinuationFormat
{
    private static string GetExtension(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant();

    private static ExportFormat? TryGetFormatForExtension(string extension) =>
        extension switch
        {
            ".json" => ExportFormat.Json,
            ".html" or ".htm" => ExportFormat.HtmlDark,
            ".csv" => ExportFormat.Csv,
            ".db" => ExportFormat.Db,
            _ => null,
        };

    private static InvalidExportException UnsupportedExtension(string extension) =>
        new($"Continuing {extension} exports is not supported.");

    public static bool IsSupportedExtension(string filePath) =>
        TryGetFormatForExtension(GetExtension(filePath)) is not null;

    public static ExportFormat FormatFor(string filePath)
    {
        var extension = GetExtension(filePath);
        return TryGetFormatForExtension(extension) ?? throw UnsupportedExtension(extension);
    }

    public static async ValueTask<ContinuationCutoff> ReadCutoffAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) =>
        FormatFor(filePath) switch
        {
            ExportFormat.Json => await ReadJsonAsync(filePath, cancellationToken),
            ExportFormat.HtmlDark => await HtmlExportInspector.InspectAsync(
                filePath,
                cancellationToken
            ),
            ExportFormat.Csv => await CsvExportInspector.InspectAsync(filePath, cancellationToken),
            ExportFormat.Db => await SqliteExportInspector.InspectAsync(
                filePath,
                cancellationToken
            ),
            var format => throw new InvalidExportException(
                $"Continuing {format} exports is not supported."
            ),
        };

    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    ) =>
        FormatFor(existingFilePath) switch
        {
            ExportFormat.Json => await JsonExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                exportedAt,
                cancellationToken
            ),
            ExportFormat.HtmlDark => await HtmlExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                cutoff,
                cancellationToken
            ),
            // CsvExportMerger returns only the number of rows it appended (it has no count to
            // recompute, unlike Json/Html). The dispatcher contract is "return the merged TOTAL",
            // so add the existing data-row count back on to keep parity with the other formats and
            // make the consumer's (total - ExistingCount) added-count come out right.
            ExportFormat.Csv => cutoff.ExistingCount
                + await CsvExportMerger.MergeAsync(
                    existingFilePath,
                    newMessagesFilePath,
                    cutoff,
                    cancellationToken
                ),
            ExportFormat.Db => await SqliteExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                cutoff,
                exportedAt,
                cancellationToken
            ),
            var format => throw new InvalidExportException(
                $"Continuing {format} exports is not supported."
            ),
        };

    private static async ValueTask<ContinuationCutoff> ReadJsonAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        var info = await JsonExportInspector.InspectAsync(filePath, cancellationToken);
        return new ContinuationCutoff(
            info.ChannelId,
            info.LastMessageId,
            info.Before,
            info.IsChronological,
            info.MessageCount,
            CutoffIsExact: true
        );
    }
}
