using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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

        if (ContinuationFileName.HasBeforeBoundHint(filePath))
        {
            throw new InvalidExportException(
                "CSV exports with a 'before' date range cannot be continued safely. "
                    + "Continue the original JSON or SQLite export instead."
            );
        }

        DateTimeOffset? firstDate = null;
        DateTimeOffset? lastDate = null;
        Snowflake? lastMessageId = null;
        var hasExactMessageId = false;
        var orderDirection = 0;
        long count = 0;
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);
            await using var rows = ParseCsvRowsAsync(reader, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            if (!await rows.MoveNextAsync())
                throw new InvalidExportException(
                    "The CSV export contains no messages to continue from."
                );

            var header = rows.Current;
            var messageIdColumnIndex = GetColumnIndex(header, "MessageID", -1);
            var dateColumnIndex = GetColumnIndex(header, "Date", 2);
            hasExactMessageId = messageIdColumnIndex >= 0;

            while (await rows.MoveNextAsync())
            {
                var fields = rows.Current;
                if (fields.Count <= dateColumnIndex)
                    continue;
                if (
                    DateTimeOffset.TryParse(
                        fields[dateColumnIndex],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var date
                    )
                )
                {
                    TrackDateOrder(lastDate, date, ref orderDirection);
                    firstDate ??= date;
                    lastDate = date;
                }

                if (messageIdColumnIndex >= 0)
                {
                    var messageId =
                        fields.Count > messageIdColumnIndex
                            ? TryParseMessageId(fields[messageIdColumnIndex])
                            : null;

                    if (messageId is { } id)
                        lastMessageId = id;
                    else
                        hasExactMessageId = false;
                }

                count++;
            }
        }
        catch (InvalidExportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        if (count <= 0)
            throw new InvalidExportException(
                "The CSV export contains no messages to continue from."
            );

        if (lastDate is null)
            throw new InvalidExportException("The CSV export has no parseable message dates.");

        var isChronological = firstDate <= lastDate;
        var hasExactCutoff = hasExactMessageId && lastMessageId is not null;
        var cutoff =
            lastMessageId is { } exactMessageId && hasExactMessageId
                ? exactMessageId
                : Snowflake.FromDate(lastDate.Value);
        return new ContinuationCutoff(
            channelId,
            cutoff,
            null,
            isChronological,
            count,
            CutoffIsExact: hasExactCutoff
        );
    }

    internal static int GetColumnIndex(
        IReadOnlyList<string> header,
        string columnName,
        int fallbackIndex
    )
    {
        var index = header
            .Select((name, index) => (name, index))
            .FirstOrDefault(pair =>
                string.Equals(pair.name, columnName, StringComparison.OrdinalIgnoreCase)
            )
            .index;

        return
            index > 0
            || string.Equals(
                header.FirstOrDefault(),
                columnName,
                StringComparison.OrdinalIgnoreCase
            )
            ? index
            : fallbackIndex;
    }

    internal static Snowflake? TryParseMessageId(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? new Snowflake(id)
            : null;

    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
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
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static async IAsyncEnumerable<IReadOnlyList<string>> ParseCsvRowsAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var buffer = new char[8192];
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var quotePending = false;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (charsRead <= 0)
                break;

            for (var i = 0; i < charsRead; i++)
            {
                var c = buffer[i];
                if (quotePending)
                {
                    if (c == '"')
                    {
                        field.Append('"');
                        quotePending = false;
                        continue;
                    }

                    inQuotes = false;
                    quotePending = false;
                }

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        quotePending = true;
                        continue;
                    }

                    field.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        yield return row;
                        row = [];
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
        }

        if (field.Length > 0 || row.Count > 0 || quotePending)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    private static void TrackDateOrder(
        DateTimeOffset? previousDate,
        DateTimeOffset currentDate,
        ref int orderDirection
    )
    {
        if (previousDate is null)
            return;

        var comparison = currentDate.CompareTo(previousDate.Value);
        if (comparison == 0)
            return;

        var direction = Math.Sign(comparison);
        if (orderDirection == 0)
        {
            orderDirection = direction;
            return;
        }

        if (orderDirection != direction)
        {
            throw new InvalidExportException(
                "The CSV export's messages are not consistently ordered."
            );
        }
    }
}
