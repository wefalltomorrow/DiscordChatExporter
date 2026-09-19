using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Library;

// Read-only full-text search over SQLite (.db) exports produced by SqliteMessageWriter.
public static class SqliteExportReader
{
    // Searches one .db. Returns [] for a blank query, a missing file, or any SQLite error
    // (a non-.db / corrupt file is treated as "no results", never throws).
    public static async ValueTask<IReadOnlyList<SqliteSearchHit>> SearchAsync(
        string databaseFilePath,
        string query,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        var matchExpression = BuildMatchExpression(query);
        if (matchExpression is null || !File.Exists(databaseFilePath))
            return [];

        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databaseFilePath,
                    Pooling = false,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString()
            );
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.timestamp, a.name,
                       snippet(messages_fts, 0, '«', '»', '…', 16)
                FROM messages_fts
                JOIN messages m ON m.id = messages_fts.message_id
                LEFT JOIN authors a ON a.id = m.author_id
                WHERE messages_fts MATCH $q
                ORDER BY rank
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$q", matchExpression);
            command.Parameters.AddWithValue("$limit", limit);

            var hits = new List<SqliteSearchHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;

                hits.Add(
                    new SqliteSearchHit(
                        databaseFilePath,
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3)
                    )
                );
            }

            return hits;
        }
        catch (SqliteException)
        {
            // Not a valid SQLite export, missing FTS table, etc. — treat as no results.
            return [];
        }
    }

    // Searches many .db files and concatenates the results (per-file rank order preserved).
    // Files that error/are missing are skipped.
    public static async ValueTask<IReadOnlyList<SqliteSearchHit>> SearchAcrossAsync(
        IReadOnlyList<string> databaseFilePaths,
        string query,
        int limitPerDatabase,
        CancellationToken cancellationToken = default
    )
    {
        if (BuildMatchExpression(query) is null)
            return [];

        var all = new List<SqliteSearchHit>();
        foreach (var path in databaseFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            all.AddRange(await SearchAsync(path, query, limitPerDatabase, cancellationToken));
        }

        return all;
    }

    // Turns free-text into a safe FTS5 MATCH expression: split on whitespace, wrap each token in
    // double quotes (doubling embedded quotes) so FTS5 treats them as literal terms (implicit AND),
    // never as operators. Returns null for blank input (caller short-circuits to no results).
    private static string? BuildMatchExpression(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => '"' + t.Replace("\"", "\"\"") + '"');

        return string.Join(' ', tokens);
    }
}
