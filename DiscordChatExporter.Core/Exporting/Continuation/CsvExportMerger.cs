using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class CsvExportMerger
{
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newRowsFilePath,
        ContinuationCutoff cutoff,
        CancellationToken cancellationToken = default
    )
    {
        string newText;
        try
        {
            newText = await File.ReadAllTextAsync(newRowsFilePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{newRowsFilePath}'.", ex);
        }

        var newRows = CsvExportInspector.ParseCsv(newText);
        var header = newRows.Count > 0 ? newRows[0] : [];
        var messageIdColumnIndex = CsvExportInspector.GetColumnIndex(header, "MessageID", -1);
        var dateColumnIndex = CsvExportInspector.GetColumnIndex(header, "Date", 2);

        var tempPath = AtomicFile.CreateSiblingTempPath(existingFilePath, ".merging.tmp");
        long added = 0;
        try
        {
            File.Copy(existingFilePath, tempPath, true);
            var needsRowSeparator = NeedsTrailingLineBreak(tempPath);
            await using (var writer = new StreamWriter(new FileStream(tempPath, FileMode.Append)))
            {
                for (var i = 1; i < newRows.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = newRows[i];
                    if (fields.Count <= dateColumnIndex)
                        continue;

                    if (
                        cutoff.CutoffIsExact
                        && messageIdColumnIndex >= 0
                        && fields.Count > messageIdColumnIndex
                        && CsvExportInspector.TryParseMessageId(fields[messageIdColumnIndex])
                            is { } messageId
                    )
                    {
                        if (messageId.Value <= cutoff.Cutoff.Value)
                            continue;

                        if (needsRowSeparator)
                        {
                            await writer.WriteAsync("\r\n");
                            needsRowSeparator = false;
                        }

                        await writer.WriteAsync(EncodeRow(fields));
                        added++;
                        continue;
                    }

                    if (
                        DateTimeOffset.TryParse(
                            fields[dateColumnIndex],
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var date
                        )
                        && Snowflake.FromDate(date).Value <= cutoff.Cutoff.Value
                    )
                        continue;

                    if (needsRowSeparator)
                    {
                        await writer.WriteAsync("\r\n");
                        needsRowSeparator = false;
                    }

                    await writer.WriteAsync(EncodeRow(fields));
                    added++;
                }
            }
            AtomicFile.ReplaceWithBackupCleanup(tempPath, existingFilePath);
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // best-effort
            }

            if (ex is OperationCanceledException)
                throw;

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidExportException(
                    $"Could not continue the CSV export '{existingFilePath}'.",
                    ex
                );
            }

            throw;
        }
        return added;
    }

    private static bool NeedsTrailingLineBreak(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        if (stream.Length <= 0)
            return false;

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() is not ('\r' or '\n');
    }

    private static string EncodeRow(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"')
                .Append(fields[i].Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append('"');
        }
        sb.Append("\r\n");
        return sb.ToString();
    }
}
