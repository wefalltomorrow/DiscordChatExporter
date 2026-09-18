using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteMessageWriterSpecs : IDisposable
{
    private readonly string _dirPath = Path.Combine(
        Path.GetTempPath(),
        "DceSqliteTest_" + Guid.NewGuid().ToString("N")
    );

    public SqliteMessageWriterSpecs() => Directory.CreateDirectory(_dirPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dirPath, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string DbPath => Path.Combine(_dirPath, "export.db");

    // A fully offline context. shouldDownloadAssets=false makes ResolveAssetUrlAsync a no-op,
    // and shouldFormatMarkdown=false keeps message content as the raw string we assert on.
    private static ExportContext CreateContext(
        string outputPath,
        PartitionLimit? partitionLimit = null
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
            "a topic",
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
            partitionLimit ?? PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    [Fact]
    public async Task File_size_partitioning_is_rejected_for_sqlite_exports()
    {
        var context = CreateContext(DbPath, PartitionLimit.Parse("1b"));
        await using var exporter = new MessageExporter(context);
        var author = CreateUser(1, "alice");
        var message = CreateMessage(10, author, "hello");

        var act = async () => await exporter.ExportMessageAsync(message);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*SQLite*");
    }

    private static User CreateUser(ulong id, string name, bool isBot = false) =>
        new(new Snowflake(id), isBot, null, name, name, "");

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
            DateTimeOffset.UnixEpoch,
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

    [Fact]
    public async Task It_writes_messages_authors_attachments_reactions_and_metadata()
    {
        // Arrange
        var alice = CreateUser(10, "alice");
        var attachment = new Attachment(
            new Snowflake(100),
            "https://cdn.example/pic.png",
            "pic.png",
            null,
            null,
            null,
            FileSize.FromBytes(2048)
        );
        var reaction = new Reaction(new Emoji(null, "👍", false), 3);

        var msg1 = CreateMessage(1001, alice, "the quick brown fox", [attachment], [reaction]);
        var msg2 = CreateMessage(1002, alice, "another plain message");

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(msg1);
            await writer.WriteMessageAsync(msg2);
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);

        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(2);
        // Both messages share one author => deduped to a single row.
        Count(connection, "SELECT COUNT(*) FROM authors;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM attachments;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM reactions;").Should().Be(1);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(2);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT content FROM messages WHERE id = '1001';";
            ((string)command.ExecuteScalar()!).Should().Be("the quick brown fox");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count FROM reactions WHERE message_id = '1001';";
            ((long)command.ExecuteScalar()!).Should().Be(3);
        }

        // Standard (non-custom) emoji has no id => NULL.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT emoji_id FROM reactions WHERE message_id = '1001';";
            command.ExecuteScalar().Should().Be(DBNull.Value);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT guild_name FROM export_info;";
            ((string)command.ExecuteScalar()!).Should().Be("Test Guild");
        }
    }

    [Fact]
    public async Task It_indexes_attachment_and_reaction_message_ids()
    {
        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN ('attachments_message_id_idx', 'reactions_message_id_idx')
            ORDER BY name;
            """;

        var indexes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(0));

        indexes.Should().Equal("attachments_message_id_idx", "reactions_message_id_idx");
    }

    [Fact]
    public async Task It_indexes_message_content_for_full_text_search()
    {
        // Arrange
        var alice = CreateUser(10, "alice");
        var msg1 = CreateMessage(2001, alice, "discord export to sqlite");
        var msg2 = CreateMessage(2002, alice, "completely unrelated content");

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(msg1);
            await writer.WriteMessageAsync(msg2);
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT message_id FROM messages_fts WHERE messages_fts MATCH $term ORDER BY rank;";
        command.Parameters.AddWithValue("$term", "sqlite");

        var matches = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            matches.Add(reader.GetString(0));

        matches.Should().ContainSingle().Which.Should().Be("2001");
    }

    [Fact]
    public async Task It_produces_a_valid_database_for_an_empty_export()
    {
        // Act — no messages written, just preamble + postamble (the empty-channel path).
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(0);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(0);
        // The FTS table exists and is queryable.
        Count(connection, "SELECT COUNT(*) FROM messages_fts;").Should().Be(0);
    }

    [Fact]
    public async Task It_leaves_a_single_file_with_no_journal_sidecars_and_releases_the_handle()
    {
        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(CreateMessage(3001, CreateUser(10, "alice"), "hi"));
            await writer.WritePostambleAsync();
        }

        // Assert — DELETE journal + Pooling=False means only "export.db" remains...
        var leftovers = Directory
            .GetFiles(_dirPath, "export.db*")
            .Select(Path.GetFileName)
            .ToArray();
        leftovers.Should().BeEquivalentTo(["export.db"]);

        // ...and the handle is fully released, so the file can be deleted.
        var delete = () => File.Delete(DbPath);
        delete.Should().NotThrow();
    }

    [Fact]
    public async Task It_overwrites_an_existing_database_on_re_export()
    {
        var context = CreateContext(DbPath);

        // First export.
        await using (var writer = new SqliteMessageWriter(DbPath, context))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(4001, CreateUser(10, "alice"), "first run")
            );
            await writer.WritePostambleAsync();
        }

        // Second export over the same path.
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(4002, CreateUser(11, "bob"), "second run")
            );
            await writer.WritePostambleAsync();
        }

        // Assert — only the second run's data is present.
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(1);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM messages;";
        ((string)command.ExecuteScalar()!).Should().Be("4002");
    }

    [Fact]
    public async Task It_stores_a_custom_animated_emoji_reaction()
    {
        // Arrange — a custom animated emoji has an id and IsAnimated == true.
        var alice = CreateUser(10, "alice");
        var reaction = new Reaction(new Emoji(new Snowflake(123), "blob", true), 1);
        var msg = CreateMessage(5001, alice, "custom emoji message", null, [reaction]);

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(msg);
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT emoji_id, emoji_name, is_animated FROM reactions WHERE message_id = '5001';";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("123");
        reader.GetString(1).Should().Be("blob");
        reader.GetInt64(2).Should().Be(1);
    }

    [Fact]
    public async Task It_ignores_a_duplicate_message_id_without_aborting_or_double_counting()
    {
        // Arrange — the same id written twice (e.g. a pagination-boundary duplicate), each with
        // its own attachment + reaction.
        var alice = CreateUser(10, "alice");
        var attachment = new Attachment(
            new Snowflake(100),
            "https://cdn.example/pic.png",
            "pic.png",
            null,
            null,
            null,
            FileSize.FromBytes(2048)
        );
        var reaction = new Reaction(new Emoji(null, "👍", false), 3);
        var first = CreateMessage(6001, alice, "the original message", [attachment], [reaction]);
        var duplicate = CreateMessage(
            6001,
            alice,
            "a duplicate with the same id",
            [attachment],
            [reaction]
        );

        // Act — the duplicate must not throw (would abort the whole channel before the fix).
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(first);
            await writer.WriteMessageAsync(duplicate);
            await writer.WritePostambleAsync();
        }

        // Assert — exactly one of everything; the duplicate's dependents are not appended, and the
        // first write wins on content.
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM messages_fts;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM attachments;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM reactions;").Should().Be(1);
        // message_count is derived from COUNT(*), so it stays 1 despite two WriteMessageAsync calls.
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(1);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM messages WHERE id = '6001';";
        ((string)command.ExecuteScalar()!).Should().Be("the original message");
    }

    [Fact]
    public async Task It_starts_cleanly_when_a_leftover_sidecar_is_locked()
    {
        // Simulate a leftover -shm sidecar locked by another handle (e.g. from a prior crash or an
        // antivirus/sync scan). In DELETE journal mode SQLite doesn't use -shm, so the export must
        // proceed even though the pre-export cleanup can't delete the locked file.
        var lockedSidecar = DbPath + "-shm";
        File.WriteAllText(lockedSidecar, "stale");
        using var lockHandle = new FileStream(
            lockedSidecar,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(7001, CreateUser(10, "alice"), "after a crash")
            );
            await writer.WritePostambleAsync();
        }

        // Assert — the db was created and populated despite the locked sidecar.
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(1);
    }
}
