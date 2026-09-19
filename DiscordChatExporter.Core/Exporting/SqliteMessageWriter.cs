using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using Microsoft.Data.Sqlite;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

// Writes a channel export as a self-contained SQLite database with full-text search metadata.
internal class SqliteMessageWriter : MessageWriter
{
    private const string SchemaSql = """
        CREATE TABLE export_info (
            guild_id       TEXT,
            guild_name     TEXT,
            guild_icon_url TEXT,
            channel_id     TEXT,
            channel_name   TEXT,
            channel_topic  TEXT,
            category_id    TEXT,
            category       TEXT,
            after          TEXT,
            before         TEXT,
            exported_at    TEXT,
            message_count  INTEGER
        );

        CREATE TABLE authors (
            id            TEXT PRIMARY KEY,
            name          TEXT NOT NULL,
            discriminator TEXT,
            nickname      TEXT,
            color         TEXT,
            is_bot        INTEGER NOT NULL,
            avatar_url    TEXT
        );

        CREATE TABLE messages (
            id                   TEXT PRIMARY KEY,
            type                 TEXT NOT NULL,
            timestamp            TEXT NOT NULL,
            timestamp_edited     TEXT,
            call_ended_timestamp TEXT,
            is_pinned            INTEGER NOT NULL,
            content              TEXT NOT NULL,
            author_id            TEXT NOT NULL,
            reference_message_id TEXT
        );

        CREATE TABLE attachments (
            message_id      TEXT NOT NULL,
            id              TEXT NOT NULL,
            url             TEXT NOT NULL,
            file_name       TEXT NOT NULL,
            file_size_bytes INTEGER NOT NULL
        );

        CREATE TABLE reactions (
            message_id  TEXT NOT NULL,
            emoji_id    TEXT,
            emoji_name  TEXT NOT NULL,
            emoji_code  TEXT NOT NULL,
            is_animated INTEGER NOT NULL,
            count       INTEGER NOT NULL
        );

        CREATE VIRTUAL TABLE messages_fts USING fts5(content, message_id UNINDEXED);

        CREATE INDEX attachments_message_id_idx ON attachments(message_id);
        CREATE INDEX reactions_message_id_idx ON reactions(message_id);
        """;

    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm", "-journal"];

    private readonly string _databaseFilePath;

    // The same author recurs across most messages; remember the ids we've already inserted so we
    // don't issue an INSERT OR IGNORE round-trip per message (M inserts) when A distinct authors
    // would do (A << M).
    private readonly HashSet<Snowflake> _seenAuthorIds = [];
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SqliteCommand? _insertMessageCommand;
    private SqliteCommand? _insertMessageFtsCommand;
    private SqliteCommand? _insertAttachmentCommand;
    private SqliteCommand? _insertReactionCommand;
    private SqliteCommand? _insertAuthorCommand;

    public SqliteMessageWriter(string databaseFilePath, ExportContext context)
        : base(Stream.Null, context)
    {
        _databaseFilePath = databaseFilePath;
    }

    private string? NormalizeOrNull(DateTimeOffset? instant) =>
        instant is { } value
            ? Context.NormalizeDate(value).ToString("o", CultureInfo.InvariantCulture)
            : null;

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void AddReusableParameter(SqliteCommand command, string name) =>
        command.Parameters.AddWithValue(name, DBNull.Value);

    private static void SetParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters[name].Value = value ?? DBNull.Value;

    private SqliteCommand CreatePreparedCommand(string commandText, params string[] parameterNames)
    {
        var command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;

        foreach (var parameterName in parameterNames)
            AddReusableParameter(command, parameterName);

        command.Prepare();
        return command;
    }

    public override async ValueTask WritePreambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Start from a clean slate so a re-export never merges into stale data and the
        // manifest/hashing layer (#1) sees a single self-contained file.
        DeleteDatabaseFiles(_databaseFilePath);

        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databaseFilePath,
                // Pooling keeps the OS file handle alive after Close(), which collides with the
                // manifest hashing (#1) and the delete-on-re-export step (#3). Disable it.
                Pooling = false,
            }.ToString()
        );
        await _connection.OpenAsync(cancellationToken);

        // DELETE journal => no -wal/-shm sidecars; the export stays a single file.
        // Must run before the transaction begins.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=DELETE;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        _transaction = (SqliteTransaction)
            await _connection.BeginTransactionAsync(cancellationToken);

        using (var schema = _connection.CreateCommand())
        {
            schema.Transaction = _transaction;
            schema.CommandText = SchemaSql;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        // One self-describing row. message_count is a placeholder finalized in the postamble.
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            INSERT INTO export_info
                (guild_id, guild_name, guild_icon_url, channel_id, channel_name, channel_topic,
                 category_id, category, after, before, exported_at, message_count)
            VALUES
                ($guildId, $guildName, $guildIconUrl, $channelId, $channelName, $channelTopic,
                 $categoryId, $category, $after, $before, $exportedAt, 0);
            """;
        AddParameter(command, "$guildId", Context.Request.Guild.Id.ToString());
        AddParameter(command, "$guildName", Context.Request.Guild.Name);
        AddParameter(
            command,
            "$guildIconUrl",
            await Context.ResolveAssetUrlAsync(Context.Request.Guild.IconUrl, cancellationToken)
        );
        AddParameter(command, "$channelId", Context.Request.Channel.Id.ToString());
        AddParameter(command, "$channelName", Context.Request.Channel.Name);
        AddParameter(command, "$channelTopic", Context.Request.Channel.Topic);
        AddParameter(command, "$categoryId", Context.Request.Channel.Parent?.Id.ToString());
        AddParameter(command, "$category", Context.Request.Channel.Parent?.Name);
        AddParameter(command, "$after", NormalizeOrNull(Context.Request.After?.ToDate()));
        AddParameter(command, "$before", NormalizeOrNull(Context.Request.Before?.ToDate()));
        AddParameter(
            command,
            "$exportedAt",
            Context.NormalizeDate(DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture)
        );
        await command.ExecuteNonQueryAsync(cancellationToken);

        _insertMessageCommand = CreatePreparedCommand(
            """
            INSERT OR IGNORE INTO messages
                (id, type, timestamp, timestamp_edited, call_ended_timestamp,
                 is_pinned, content, author_id, reference_message_id)
            VALUES
                ($id, $type, $timestamp, $timestampEdited, $callEnded,
                 $isPinned, $content, $authorId, $referenceMessageId);
            """,
            "$id",
            "$type",
            "$timestamp",
            "$timestampEdited",
            "$callEnded",
            "$isPinned",
            "$content",
            "$authorId",
            "$referenceMessageId"
        );
        _insertMessageFtsCommand = CreatePreparedCommand(
            "INSERT INTO messages_fts (content, message_id) VALUES ($content, $messageId);",
            "$content",
            "$messageId"
        );
        _insertAttachmentCommand = CreatePreparedCommand(
            """
            INSERT INTO attachments (message_id, id, url, file_name, file_size_bytes)
            VALUES ($messageId, $id, $url, $fileName, $fileSizeBytes);
            """,
            "$messageId",
            "$id",
            "$url",
            "$fileName",
            "$fileSizeBytes"
        );
        _insertReactionCommand = CreatePreparedCommand(
            """
            INSERT INTO reactions (message_id, emoji_id, emoji_name, emoji_code, is_animated, count)
            VALUES ($messageId, $emojiId, $emojiName, $emojiCode, $isAnimated, $count);
            """,
            "$messageId",
            "$emojiId",
            "$emojiName",
            "$emojiCode",
            "$isAnimated",
            "$count"
        );
        _insertAuthorCommand = CreatePreparedCommand(
            """
            INSERT OR IGNORE INTO authors (id, name, discriminator, nickname, color, is_bot, avatar_url)
            VALUES ($id, $name, $discriminator, $nickname, $color, $isBot, $avatarUrl);
            """,
            "$id",
            "$name",
            "$discriminator",
            "$nickname",
            "$color",
            "$isBot",
            "$avatarUrl"
        );
    }

    public override async ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        await base.WriteMessageAsync(message, cancellationToken);

        var content = message.IsSystemNotification
            ? message.GetFallbackContent()
            : await FormatMarkdownAsync(message.Content, cancellationToken);
        var messageId = message.Id.ToString();
        var authorId = message.Author.Id.ToString();
        var referenceMessageId = message.Reference?.MessageId?.ToString();

        await WriteAuthorAsync(message.Author, cancellationToken);

        var insertMessageCommand = _insertMessageCommand!;
        // OR IGNORE: a repeated message id (e.g. a pagination-boundary duplicate) is skipped
        // rather than aborting the whole channel, matching how the other writers tolerate it.
        SetParameter(insertMessageCommand, "$id", messageId);
        SetParameter(insertMessageCommand, "$type", message.Kind.ToString());
        SetParameter(
            insertMessageCommand,
            "$timestamp",
            Context.NormalizeDate(message.Timestamp).ToString("o", CultureInfo.InvariantCulture)
        );
        SetParameter(
            insertMessageCommand,
            "$timestampEdited",
            NormalizeOrNull(message.EditedTimestamp)
        );
        SetParameter(
            insertMessageCommand,
            "$callEnded",
            NormalizeOrNull(message.CallEndedTimestamp)
        );
        SetParameter(insertMessageCommand, "$isPinned", message.IsPinned ? 1 : 0);
        SetParameter(insertMessageCommand, "$content", content);
        SetParameter(insertMessageCommand, "$authorId", authorId);
        SetParameter(insertMessageCommand, "$referenceMessageId", referenceMessageId);
        var messageRows = await insertMessageCommand.ExecuteNonQueryAsync(cancellationToken);

        // The message id already existed and was ignored above; skip its dependent rows so the
        // FTS index, attachments, and reactions don't accumulate duplicates for it.
        if (messageRows == 0)
            return;

        var insertMessageFtsCommand = _insertMessageFtsCommand!;
        SetParameter(insertMessageFtsCommand, "$content", content);
        SetParameter(insertMessageFtsCommand, "$messageId", messageId);
        await insertMessageFtsCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var attachment in message.Attachments)
        {
            var insertAttachmentCommand = _insertAttachmentCommand!;
            SetParameter(insertAttachmentCommand, "$messageId", messageId);
            SetParameter(insertAttachmentCommand, "$id", attachment.Id.ToString());
            SetParameter(
                insertAttachmentCommand,
                "$url",
                await Context.ResolveAssetUrlAsync(attachment.Url, cancellationToken)
            );
            SetParameter(insertAttachmentCommand, "$fileName", attachment.FileName);
            SetParameter(insertAttachmentCommand, "$fileSizeBytes", attachment.FileSize.TotalBytes);
            await insertAttachmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Reactions: counts only. We deliberately do NOT call GetMessageReactionsAsync
        // (the per-user fetch), keeping the writer network-free.
        foreach (var reaction in message.Reactions)
        {
            var insertReactionCommand = _insertReactionCommand!;
            SetParameter(insertReactionCommand, "$messageId", messageId);
            SetParameter(insertReactionCommand, "$emojiId", reaction.Emoji.Id?.ToString());
            SetParameter(insertReactionCommand, "$emojiName", reaction.Emoji.Name);
            SetParameter(insertReactionCommand, "$emojiCode", reaction.Emoji.Code);
            SetParameter(insertReactionCommand, "$isAnimated", reaction.Emoji.IsAnimated ? 1 : 0);
            SetParameter(insertReactionCommand, "$count", reaction.Count);
            await insertReactionCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async ValueTask WriteAuthorAsync(User user, CancellationToken cancellationToken)
    {
        // Already inserted this author in this export; the row (deduped by id) is unchanged.
        if (!_seenAuthorIds.Add(user.Id))
            return;

        var userId = user.Id.ToString();
        var member = Context.TryGetMember(user.Id);

        var insertAuthorCommand = _insertAuthorCommand!;
        // OR IGNORE dedupes by the author id primary key.
        SetParameter(insertAuthorCommand, "$id", userId);
        SetParameter(insertAuthorCommand, "$name", user.Name);
        SetParameter(insertAuthorCommand, "$discriminator", user.DiscriminatorFormatted);
        SetParameter(insertAuthorCommand, "$nickname", member?.DisplayName ?? user.DisplayName);
        SetParameter(
            insertAuthorCommand,
            "$color",
            Context.TryGetUserColor(user.Id)?.ToHexString()
        );
        SetParameter(insertAuthorCommand, "$isBot", user.IsBot ? 1 : 0);
        SetParameter(
            insertAuthorCommand,
            "$avatarUrl",
            await Context.ResolveAssetUrlAsync(
                member?.AvatarUrl ?? user.AvatarUrl,
                cancellationToken
            )
        );
        await insertAuthorCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async ValueTask WritePostambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_connection is null || _transaction is null)
            return;

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = _transaction;
            // Derive the count from the table so it always matches the rows actually committed —
            // robust to OR IGNORE'd duplicates and to a partial transaction on a failed export.
            command.CommandText =
                "UPDATE export_info SET message_count = (SELECT COUNT(*) FROM messages);";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await _transaction.CommitAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_insertMessageCommand is not null)
            await _insertMessageCommand.DisposeAsync();
        if (_insertMessageFtsCommand is not null)
            await _insertMessageFtsCommand.DisposeAsync();
        if (_insertAttachmentCommand is not null)
            await _insertAttachmentCommand.DisposeAsync();
        if (_insertReactionCommand is not null)
            await _insertReactionCommand.DisposeAsync();
        if (_insertAuthorCommand is not null)
            await _insertAuthorCommand.DisposeAsync();

        // Disposing the transaction without a successful Commit rolls it back (e.g. the postamble
        // threw at/before Commit). Note the exporter still runs the postamble on the mid-export
        // error path, so a failed export commits whatever was written — consistent with the other
        // writers, which finalize a partial file on failure. Disposing the connection releases the
        // file handle (Pooling=False).
        if (_transaction is not null)
            await _transaction.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();

        await base.DisposeAsync();
    }

    private static void DeleteDatabaseFiles(string databaseFilePath)
    {
        foreach (var suffix in DatabaseFileSuffixes)
        {
            var path = databaseFilePath + suffix;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a locked or leftover sidecar (e.g. from a prior crash or an
                // antivirus/sync scan) must not abort the export before it starts. If the main db
                // file itself is locked, the subsequent OpenAsync surfaces a clear error instead.
            }
        }
    }
}
