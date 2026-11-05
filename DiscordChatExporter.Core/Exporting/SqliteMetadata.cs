using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting;

// Stable snapshot ids allow independent exports to share identical metadata without remapping ids.
internal static class SqliteMetadata
{
    internal const string AuthorColumns =
        "id, name, discriminator, nickname, color, is_bot, avatar_url";
    internal const string AuthorRevisionExpression =
        "dce_revision(json_array(id, name, discriminator, nickname, color, is_bot, avatar_url))";
    internal const string InfoColumns =
        "guild_id, guild_name, guild_icon_url, channel_id, channel_name, channel_topic, category_id, category";
    internal const string InfoRevisionExpression =
        "dce_revision(json_array(guild_id, guild_name, guild_icon_url, channel_id, channel_name, channel_topic, category_id, category))";

    public static void RegisterFunctions(SqliteConnection connection) =>
        connection.CreateFunction<string, string>(
            "dce_revision",
            value => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
            isDeterministic: true
        );

    public static async ValueTask<bool> HasRevisionsAsync(
        SqliteConnection connection,
        string schema,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // schema is an internal constant (main/incoming), never user input.
        command.CommandText = $"PRAGMA {schema}.table_info(messages);";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1) == "author_revision_id")
                return true;
        }
        return false;
    }

    // Upgrade the destination transactionally. Incoming databases are never modified.
    public static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        RegisterFunctions(connection);
        var hasRevisions = await HasRevisionsAsync(
            connection,
            "main",
            transaction,
            cancellationToken
        );
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS author_revisions (
                revision_id TEXT PRIMARY KEY,
                id TEXT NOT NULL, name TEXT NOT NULL, discriminator TEXT, nickname TEXT,
                color TEXT, is_bot INTEGER NOT NULL, avatar_url TEXT
            );
            CREATE INDEX IF NOT EXISTS author_revisions_author_id_idx ON author_revisions(id);
            CREATE TABLE IF NOT EXISTS export_info_revisions (
                revision_id TEXT PRIMARY KEY,
                guild_id TEXT, guild_name TEXT, guild_icon_url TEXT, channel_id TEXT,
                channel_name TEXT, channel_topic TEXT, category_id TEXT, category TEXT
            );
            """;
        if (!hasRevisions)
            command.CommandText += """
                ALTER TABLE messages ADD COLUMN author_revision_id TEXT;
                ALTER TABLE messages ADD COLUMN export_info_revision_id TEXT;
                """;
        command.CommandText += $"""
            INSERT OR IGNORE INTO author_revisions
                SELECT {AuthorRevisionExpression}, {AuthorColumns} FROM authors;
            INSERT OR IGNORE INTO export_info_revisions
                SELECT {InfoRevisionExpression}, {InfoColumns} FROM export_info;
            UPDATE messages SET author_revision_id =
                (SELECT {AuthorRevisionExpression} FROM authors WHERE authors.id = messages.author_id)
                WHERE author_revision_id IS NULL;
            UPDATE messages SET export_info_revision_id =
                (SELECT {InfoRevisionExpression} FROM export_info LIMIT 1)
                WHERE export_info_revision_id IS NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
