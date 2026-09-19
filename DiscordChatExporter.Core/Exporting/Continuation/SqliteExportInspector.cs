using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class SqliteExportInspector
{
    private static readonly string[] RequiredTableNames =
    [
        "export_info",
        "authors",
        "messages",
        "attachments",
        "reactions",
        "messages_fts",
    ];

    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            throw new InvalidExportException($"The file '{filePath}' does not exist.");

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ValidateSchemaAsync(connection, cancellationToken);

            string channelIdText;
            string? afterText;
            string? beforeText;
            await using (var info = connection.CreateCommand())
            {
                info.CommandText = "SELECT channel_id, after, before FROM export_info LIMIT 1;";
                await using var reader = await info.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidExportException(
                        "The SQLite export has no export_info row to continue from."
                    );

                channelIdText = reader.GetString(0);
                afterText = reader.IsDBNull(1) ? null : reader.GetString(1);
                beforeText = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            long count;
            string? maxIdText;
            await using (var messages = connection.CreateCommand())
            {
                messages.CommandText =
                    "SELECT COUNT(*), CAST(MAX(CAST(id AS INTEGER)) AS TEXT) FROM messages;";
                await using var reader = await messages.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                count = reader.GetInt64(0);
                maxIdText = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var channelId = new Snowflake(ulong.Parse(channelIdText, CultureInfo.InvariantCulture));
            var before = ParseOptionalSnowflakeDate(beforeText, "before");
            var after = ParseOptionalSnowflakeDate(afterText, "after");

            Snowflake cutoff;
            bool exact;
            if (maxIdText is not null)
            {
                cutoff = new Snowflake(ulong.Parse(maxIdText, CultureInfo.InvariantCulture));
                exact = true;
            }
            else
            {
                cutoff = after ?? new Snowflake(0);
                exact = false;
            }

            return new ContinuationCutoff(channelId, cutoff, before, true, count, exact);
        }
        catch (SqliteException ex)
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
        catch (Exception ex)
            when (ex
                    is FormatException
                        or InvalidCastException
                        or InvalidOperationException
                        or OverflowException
            )
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
    }

    private static async ValueTask ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var missingTableNames = new HashSet<string>(
            RequiredTableNames,
            StringComparer.OrdinalIgnoreCase
        );

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type IN ('table', 'virtual table')
              AND name IN ('export_info', 'authors', 'messages', 'attachments', 'reactions', 'messages_fts');
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            missingTableNames.Remove(reader.GetString(0));

        if (missingTableNames.Count > 0)
        {
            throw new InvalidExportException(
                $"The SQLite export is missing the required '{missingTableNames.First()}' table."
            );
        }
    }

    private static Snowflake? ParseOptionalSnowflakeDate(string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date
            )
        )
        {
            return Snowflake.FromDate(date);
        }

        throw new InvalidExportException(
            $"The SQLite export has a malformed '{fieldName}' date bound."
        );
    }
}
