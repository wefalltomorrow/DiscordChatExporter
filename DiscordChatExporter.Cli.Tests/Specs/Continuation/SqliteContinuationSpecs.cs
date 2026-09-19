using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class SqliteContinuationSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceSqliteCont_" + Guid.NewGuid().ToString("N")
    );

    public SqliteContinuationSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ExportContext CreateContext(
        string outputPath,
        bool isReverseMessageOrder = false
    )
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            "topic",
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Db,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder,
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Attachment CreateAttachment(ulong id) =>
        new(
            new Snowflake(id),
            $"https://example.com/{id}.txt",
            $"{id}.txt",
            null,
            null,
            null,
            FileSize.FromBytes(1)
        );

    private static Reaction CreateReaction(string emojiName, int count) =>
        new(new Emoji(null, emojiName, false), count);

    private static Message CreateMessage(
        ulong id,
        User author,
        string content,
        IReadOnlyList<Attachment>? attachments = null,
        IReadOnlyList<Reaction>? reactions = null
    ) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
            null,
            null,
            false,
            content,
            attachments ?? [],
            [],
            [],
            reactions ?? [],
            [],
            null,
            null,
            null,
            null
        );

    private async Task<string> WriteMessageDbAsync(string fileName, params Message[] messages)
    {
        var path = Path.Combine(_dir, fileName);
        await using var writer = new SqliteMessageWriter(path, CreateContext(path));
        await writer.WritePreambleAsync();
        foreach (var message in messages)
            await writer.WriteMessageAsync(message);
        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteDbAsync(
        string fileName,
        params (ulong id, string content)[] messages
    )
    {
        var path = Path.Combine(_dir, fileName);
        var author = CreateUser(10, "alice");
        await using var writer = new SqliteMessageWriter(path, CreateContext(path));
        await writer.WritePreambleAsync();
        foreach (var (id, content) in messages)
            await writer.WriteMessageAsync(CreateMessage(id, author, content));
        await writer.WritePostambleAsync();
        return path;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static async Task ExecuteNonQueryAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Inspector_reads_channel_id_cutoff_and_count()
    {
        var path = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.ChannelId.Should().Be(new Snowflake(2));
        cutoff.Cutoff.Should().Be(new Snowflake(1003));
        cutoff.ExistingCount.Should().Be(3);
        cutoff.IsChronological.Should().BeTrue();
        cutoff.CutoffIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task Inspector_handles_an_empty_export()
    {
        var path = await WriteDbAsync("empty.db");

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.ExistingCount.Should().Be(0);
        cutoff.CutoffIsExact.Should().BeFalse();
    }

    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    public async Task Inspector_rejects_non_empty_malformed_date_bounds(string column)
    {
        var path = await WriteDbAsync("chat.db", (1001, "a"));
        await ExecuteNonQueryAsync(path, $"UPDATE export_info SET {column} = 'not-a-date';");

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Inspector_treats_blank_date_bounds_as_missing()
    {
        var path = await WriteDbAsync("chat.db", (1001, "a"));
        await ExecuteNonQueryAsync(path, "UPDATE export_info SET after = ' ', before = '';");

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.Before.Should().BeNull();
    }

    [Fact]
    public async Task Inspector_uses_newest_message_id_for_reverse_inserted_exports()
    {
        var path = await WriteDbAsync("reverse.db", (1003, "c"), (1002, "b"), (1001, "a"));

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.Cutoff.Should().Be(new Snowflake(1003));
        cutoff.IsChronological.Should().BeTrue();
    }

    [Fact]
    public async Task Inspector_rejects_a_non_sqlite_file()
    {
        var path = Path.Combine(_dir, "notadb.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Inspector_rejects_a_sqlite_file_missing_export_info()
    {
        var path = Path.Combine(_dir, "wrong-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE messages (id TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Inspector_rejects_a_partial_sqlite_export_schema()
    {
        var path = Path.Combine(_dir, "partial-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE export_info (
                    channel_id TEXT NOT NULL,
                    after TEXT,
                    before TEXT
                );
                CREATE TABLE messages (id TEXT PRIMARY KEY);
                INSERT INTO export_info (channel_id, after, before) VALUES ('2', NULL, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Merger_wraps_a_corrupt_existing_database_as_InvalidExportException()
    {
        var existing = Path.Combine(_dir, "chat [111].db");
        await File.WriteAllTextAsync(existing, "this is not a sqlite database");
        var existingBefore = await File.ReadAllBytesAsync(existing);

        var incoming = Path.Combine(_dir, "incoming.db");
        await File.WriteAllTextAsync(incoming, "also not a database");

        var act = async () =>
            await SqliteExportMerger.MergeAsync(
                existing,
                incoming,
                new ContinuationCutoff(new Snowflake(111), new Snowflake(0), null, true, 0, true),
                DateTimeOffset.UnixEpoch
            );

        await act.Should().ThrowAsync<InvalidExportException>();
        (await File.ReadAllBytesAsync(existing)).Should().Equal(existingBefore);
        File.Exists(existing + ".merging.tmp").Should().BeFalse();
        File.Exists(existing + ".bak").Should().BeFalse();
    }

    [Fact]
    public async Task Merger_cleanup_does_not_mask_primary_failure_with_cancellation()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "old"));
        var incoming = await WriteDbAsync("new.db", (1002, "new"));
        await ExecuteNonQueryAsync(incoming, "DROP TABLE messages_fts;");
        var cutoff = await SqliteExportInspector.InspectAsync(existing);
        using var cancellation = new CancellationTokenSource();

        var act = async () =>
            await SqliteExportMerger.MergeAsync(
                existing,
                incoming,
                cutoff,
                DateTimeOffset.UnixEpoch,
                beforeDetachAsync: () =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellationToken: cancellation.Token
            );

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Merger_propagates_user_cancellation()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "old"));
        var incoming = await WriteDbAsync("new.db", (1002, "new"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () =>
            await SqliteExportMerger.MergeAsync(
                existing,
                incoming,
                cutoff,
                DateTimeOffset.UnixEpoch,
                cancellation.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Merger_appends_new_messages_and_updates_count_and_fts()
    {
        var existing = await WriteDbAsync(
            "chat.db",
            (1001, "old one"),
            (1002, "old two"),
            (1003, "old three")
        );
        var incoming = await WriteDbAsync("new.db", (1004, "fresh four"), (1005, "fresh five"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(5);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(5);
        Count(connection, "SELECT COUNT(*) FROM messages_fts;").Should().Be(5);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(5);

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'fresh';";
        ((long)query.ExecuteScalar()!).Should().Be(2);
    }

    [Fact]
    public async Task Merger_ignores_a_boundary_duplicate_message()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));
        var incoming = await WriteDbAsync("new.db", (1003, "c again"), (1004, "d"), (1005, "e"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(5);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(5);

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT content FROM messages WHERE id = '1003';";
        ((string)query.ExecuteScalar()!).Should().Be("c");
    }

    [Fact]
    public async Task Merger_ignores_boundary_duplicate_attachments_and_reactions()
    {
        var author = CreateUser(10, "alice");
        var existing = await WriteMessageDbAsync(
            "chat.db",
            CreateMessage(1001, author, "a"),
            CreateMessage(1002, author, "b"),
            CreateMessage(1003, author, "c", [CreateAttachment(5003)], [CreateReaction("smile", 2)])
        );
        var incoming = await WriteMessageDbAsync(
            "new.db",
            CreateMessage(
                1003,
                author,
                "c again",
                [CreateAttachment(6003)],
                [CreateReaction("smile", 7)]
            ),
            CreateMessage(1004, author, "d", [CreateAttachment(5004)], [CreateReaction("fire", 1)])
        );
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(4);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM attachments;").Should().Be(2);
        Count(connection, "SELECT COUNT(*) FROM reactions;").Should().Be(2);
        Count(connection, "SELECT COUNT(*) FROM attachments WHERE message_id = '1003';")
            .Should()
            .Be(1);
        Count(connection, "SELECT COUNT(*) FROM reactions WHERE message_id = '1003';")
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Merger_ignores_orphan_dependent_rows_from_the_incoming_database()
    {
        var author = CreateUser(10, "alice");
        var existing = await WriteMessageDbAsync("chat.db", CreateMessage(1003, author, "old"));
        var incoming = await WriteMessageDbAsync(
            "new.db",
            CreateMessage(
                1004,
                author,
                "new",
                [CreateAttachment(5004)],
                [CreateReaction("fire", 1)]
            )
        );
        await ExecuteNonQueryAsync(
            incoming,
            """
            INSERT INTO attachments (message_id, id, url, file_name, file_size_bytes)
            VALUES ('9000', '7000', 'https://example.com/orphan.txt', 'orphan.txt', 1);
            INSERT INTO reactions (message_id, emoji_id, emoji_name, emoji_code, is_animated, count)
            VALUES ('9000', NULL, 'ghost', 'ghost', 0, 1);
            INSERT INTO messages_fts (content, message_id)
            VALUES ('orphan', '9000');
            """
        );
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(2);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM attachments WHERE message_id = '9000';")
            .Should()
            .Be(0);
        Count(connection, "SELECT COUNT(*) FROM reactions WHERE message_id = '9000';")
            .Should()
            .Be(0);
        Count(connection, "SELECT COUNT(*) FROM messages_fts WHERE message_id = '9000';")
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task Inspector_reads_the_newest_cutoff_after_a_merge()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));
        var incoming = await WriteDbAsync("new.db", (1004, "d"), (1005, "e"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        await SqliteExportMerger.MergeAsync(existing, incoming, cutoff, DateTimeOffset.UnixEpoch);

        var mergedCutoff = await SqliteExportInspector.InspectAsync(existing);
        mergedCutoff.Cutoff.Should().Be(new Snowflake(1005));
        mergedCutoff.ExistingCount.Should().Be(5);
        mergedCutoff.CutoffIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task Merger_refreshes_metadata_when_incoming_database_is_empty()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"));
        var incoming = await WriteDbAsync("empty.db");
        var cutoff = await SqliteExportInspector.InspectAsync(existing);
        var exportedAt = DateTimeOffset.UnixEpoch.AddDays(1);

        var total = await SqliteExportMerger.MergeAsync(existing, incoming, cutoff, exportedAt);

        total.Should().Be(2);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(2);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT exported_at FROM export_info;";
        ((string)query.ExecuteScalar()!).Should().Be(exportedAt.ToString("o"));
    }

    [Fact]
    public async Task Inspector_rejects_malformed_sqlite_metadata()
    {
        var path = await WriteDbAsync("chat.db", (1001, "a"));
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE export_info SET channel_id = 'not-a-snowflake';";
            await command.ExecuteNonQueryAsync();
        }

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public void ContinuationFormat_supports_sqlite_exports()
    {
        ContinuationFormat.IsSupportedExtension("chat.db").Should().BeTrue();
        ContinuationFormat.FormatFor("chat.db").Should().Be(ExportFormat.Db);
    }
}
