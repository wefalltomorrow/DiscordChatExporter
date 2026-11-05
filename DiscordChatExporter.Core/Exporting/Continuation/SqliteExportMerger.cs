using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class SqliteExportMerger
{
    public static async ValueTask<long> MergeAsync(
        string existingDatabaseFilePath,
        string newDatabaseFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    ) =>
        await MergeAsync(
            existingDatabaseFilePath,
            newDatabaseFilePath,
            cutoff,
            exportedAt,
            beforeDetachAsync: null,
            cancellationToken
        );

    internal static async ValueTask<long> MergeAsync(
        string existingDatabaseFilePath,
        string newDatabaseFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        Func<ValueTask>? beforeDetachAsync,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = existingDatabaseFilePath,
                    Pooling = false,
                }.ToString()
            );
            await connection.OpenAsync(cancellationToken);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=DELETE;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS incoming;";
                attach.Parameters.AddWithValue("$path", newDatabaseFilePath);
                await attach.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                await using var transaction = (SqliteTransaction)
                    await connection.BeginTransactionAsync(cancellationToken);
                await SqliteMetadata.EnsureSchemaAsync(connection, transaction, cancellationToken);
                var incomingHasRevisions = await SqliteMetadata.HasRevisionsAsync(
                    connection,
                    "incoming",
                    transaction,
                    cancellationToken
                );
                var authorRevisions = incomingHasRevisions
                    ? "SELECT * FROM incoming.author_revisions"
                    : $"SELECT {SqliteMetadata.AuthorRevisionExpression}, {SqliteMetadata.AuthorColumns} FROM incoming.authors";
                var infoRevisions = incomingHasRevisions
                    ? "SELECT * FROM incoming.export_info_revisions"
                    : $"SELECT {SqliteMetadata.InfoRevisionExpression}, {SqliteMetadata.InfoColumns} FROM incoming.export_info";
                var messageAuthorRevision = incomingHasRevisions
                    ? "m.author_revision_id"
                    : $"(SELECT {SqliteMetadata.AuthorRevisionExpression} FROM incoming.authors WHERE id = m.author_id)";
                var messageInfoRevision = incomingHasRevisions
                    ? "m.export_info_revision_id"
                    : $"(SELECT {SqliteMetadata.InfoRevisionExpression} FROM incoming.export_info LIMIT 1)";

                await using (var copy = connection.CreateCommand())
                {
                    copy.Transaction = transaction;
                    copy.CommandText = $"""
                        CREATE TEMP TABLE accepted_messages AS
                            SELECT id FROM incoming.messages
                            WHERE CAST(id AS INTEGER) > $cutoff
                                AND id NOT IN (SELECT id FROM main.messages);
                        INSERT OR IGNORE INTO main.authors SELECT * FROM incoming.authors;
                        INSERT OR IGNORE INTO main.author_revisions {authorRevisions};
                        INSERT OR IGNORE INTO main.export_info_revisions {infoRevisions};
                        INSERT OR IGNORE INTO main.messages
                            (id, type, timestamp, timestamp_edited, call_ended_timestamp,
                             is_pinned, content, author_id, reference_message_id,
                             author_revision_id, export_info_revision_id)
                            SELECT m.id, m.type, m.timestamp, m.timestamp_edited, m.call_ended_timestamp,
                                m.is_pinned, m.content, m.author_id, m.reference_message_id,
                                {messageAuthorRevision}, {messageInfoRevision}
                            FROM incoming.messages m WHERE m.id IN (SELECT id FROM accepted_messages);
                        INSERT INTO main.attachments
                            SELECT a.* FROM incoming.attachments AS a
                            WHERE EXISTS (
                                SELECT 1 FROM accepted_messages AS m WHERE m.id = a.message_id
                            );
                        INSERT INTO main.reactions
                            SELECT r.* FROM incoming.reactions AS r
                            WHERE EXISTS (
                                SELECT 1 FROM accepted_messages AS m WHERE m.id = r.message_id
                            );
                        INSERT INTO main.messages_fts (content, message_id)
                            SELECT f.content, f.message_id FROM incoming.messages_fts AS f
                            WHERE EXISTS (
                                SELECT 1 FROM accepted_messages AS m WHERE m.id = f.message_id
                            );
                        DROP TABLE accepted_messages;
                        """;
                    copy.Parameters.AddWithValue("$cutoff", (long)cutoff.Cutoff.Value);
                    await copy.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = $"""
                        UPDATE main.export_info SET
                            ({SqliteMetadata.InfoColumns}) =
                                (SELECT {SqliteMetadata.InfoColumns} FROM incoming.export_info LIMIT 1),
                            message_count = (SELECT COUNT(*) FROM main.messages), exported_at = $now;
                        """;
                    update.Parameters.AddWithValue(
                        "$now",
                        exportedAt.ToString("o", CultureInfo.InvariantCulture)
                    );
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                if (beforeDetachAsync is not null)
                    await beforeDetachAsync();

                await using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE incoming;";
                await detach.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM main.messages;";
                return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException(
                $"Could not continue the SQLite export '{existingDatabaseFilePath}'. "
                    + "The file may be locked, corrupted, or not a DiscordChatExporter database.",
                ex
            );
        }
    }
}
