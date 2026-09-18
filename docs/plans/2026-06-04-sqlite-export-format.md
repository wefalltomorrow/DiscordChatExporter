# SQLite Export Format (#2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new `ExportFormat.Db` that writes a single self-contained SQLite database (with a full-text-search index over message content) as a first-class export format, providing the queryable substrate the #5 Library home view will search.

**Architecture:** A plain `internal SqliteMessageWriter : MessageWriter` (same shape as `JsonMessageWriter`/`CsvMessageWriter`) opens a `Microsoft.Data.Sqlite` connection in `WritePreambleAsync`, writes rows per message inside one transaction, and commits in `WritePostambleAsync`. It passes `Stream.Null` to the base writer (SQLite owns its own file handle, so there is no backing `Stream`). `MessageExporter.CreateMessageWriter` gets a `Db` arm that — unlike every other format — does **not** call `File.Create` (SQLite must open the path itself). The writer is network-free: reactions are stored as counts only (no per-user fetch), and asset URLs go through the existing `Context.ResolveAssetUrlAsync` (a no-op returning the URL unchanged when asset download is disabled).

**Tech Stack:** C# / .NET 10, `Microsoft.Data.Sqlite` 10.0.8 (SQLitePCLRaw bundled `e_sqlite3` native engine, FTS5), xUnit + FluentAssertions.

**Pre-verified (do not re-litigate):** A throwaway spike published a self-contained, `PublishTrimmed=true`, `win-x64` console that opened a connection, created a normal table + an FTS5 virtual table, inserted in a transaction, ran a `MATCH` join query, and confirmed (a) `e_sqlite3.dll` ships in the trimmed output, (b) `Pooling=False` + `PRAGMA journal_mode=DELETE` leaves no `-wal`/`-shm` sidecars and releases the file handle on dispose. So trimming, FTS5, pooling, and journal mode are settled at the SQLite layer — this plan only has to wire them into the writer.

**v1 scope / deferred fields (intentional, per the brainstorm spec for #2):** stored = `export_info` (one row), `authors` (deduped), `messages`, `attachments`, `reactions` (counts), and an FTS5 index over message content. **Deferred:** embeds, stickers, mentions, forwarded-message detail, interactions, inline emoji, and per-reaction user lists. Consequence to document: a message whose only text lives in an embed will not match FTS content search in v1. The full-fidelity formats (JSON/HTML) remain the complete-archive option; SQLite is the structured/searchable option.

**Conventions to respect:**
- Central package management: package versions live in `Directory.Packages.props`; `.csproj` files reference packages **without** a `Version` attribute.
- The repo runs CSharpier on build (`CSharpier.MsBuild`). Match existing formatting; a build will reformat if needed.
- Mirror `JsonMessageWriter` for which `Message`/`User`/`Attachment`/`Reaction`/`Emoji` fields map where — it is the reference model.

---

## File Structure

**Create:**
- `DiscordChatExporter.Core/Exporting/SqliteMessageWriter.cs` — the writer (lives beside `JsonMessageWriter.cs`, same namespace `DiscordChatExporter.Core.Exporting`).
- `DiscordChatExporter.Cli.Tests/Specs/SqliteMessageWriterSpecs.cs` — token-free unit tests driving the real writer through a synthetic offline `ExportContext`.
- `DiscordChatExporter.Cli.Tests/Specs/SqliteContentSpecs.cs` — token-gated integration test (real Discord data), mirroring `JsonContentSpecs`.

**Modify:**
- `Directory.Packages.props` — add `Microsoft.Data.Sqlite` package version.
- `DiscordChatExporter.Core/DiscordChatExporter.Core.csproj` — add the package reference + `InternalsVisibleTo` for the test assembly.
- `DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj` — add the package reference (tests open the `.db` to assert on it).
- `DiscordChatExporter.Core/Exporting/ExportFormat.cs` — add the `Db` enum member + arms in `GetFileExtension`/`GetDisplayName`.
- `DiscordChatExporter.Core/Exporting/MessageExporter.cs` — add the `Db` arm in `CreateMessageWriter` (no `File.Create`).
- `DiscordChatExporter.Cli.Tests/Infra/ExportWrapper.cs` — add `ExportAsDbAsync` (for the token-gated spec).

**No change needed (verified):** GUI format dropdown (`ExportSetupViewModel.AvailableFormats = Enum.GetValues<ExportFormat>()` + `ExportFormatToStringConverter` → `GetDisplayName()`), CLI `--format` (CliFx binds enum members by name), and the Round-3 "export all formats" path (`Enum.GetValues<ExportFormat>()`) all pick up `Db` automatically. `ContinuationFormat` switches on file extension with a `throw` default, so `.db` is simply not offered for "Continue export" — correct for v1.

---

### Task 1: Dependency wiring + `ExportFormat.Db` plumbing

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `DiscordChatExporter.Core/DiscordChatExporter.Core.csproj`
- Modify: `DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj`
- Modify: `DiscordChatExporter.Core/Exporting/ExportFormat.cs:5-39`
- Test: `DiscordChatExporter.Cli.Tests/Specs/ExportFormatSpecs.cs` (create)

> Note: `Db` must be added to the enum **together** with the two `switch` arms in the same task — the GUI dropdown calls `GetDisplayName()` for every `Enum.GetValues<ExportFormat>()` entry, so a `Db` member without its arms would throw `ArgumentOutOfRangeException` at runtime.

- [ ] **Step 1: Add the package version (central management)**

In `Directory.Packages.props`, add this line inside the `<ItemGroup>` (keep alphabetical order — between `Markdig` and `Material.Avalonia`):

```xml
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.8" />
```

- [ ] **Step 2: Reference the package + expose internals from Core**

In `DiscordChatExporter.Core/DiscordChatExporter.Core.csproj`, add the package reference inside the existing `<ItemGroup>` (alphabetical, after `Gress`... — match the existing order; placement need not be exact, but keep it tidy):

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" />
```

Then add a new `<ItemGroup>` (the SDK turns `InternalsVisibleTo` items into the assembly attribute; `GenerateAssemblyInfo` is on by default):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="DiscordChatExporter.Cli.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Reference the package from the test project**

In `DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj`, add inside the package `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" />
```

- [ ] **Step 4: Write the failing test for the enum extension arms**

Create `DiscordChatExporter.Cli.Tests/Specs/ExportFormatSpecs.cs`:

```csharp
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportFormatSpecs
{
    [Fact]
    public void Db_format_has_a_db_file_extension()
    {
        ExportFormat.Db.GetFileExtension().Should().Be("db");
    }

    [Fact]
    public void Db_format_has_a_display_name()
    {
        ExportFormat.Db.GetDisplayName().Should().Be("SQLite");
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~ExportFormatSpecs"`
Expected: FAIL — `ExportFormat` has no member `Db` (compile error).

- [ ] **Step 6: Add the `Db` member and the two switch arms**

In `DiscordChatExporter.Core/Exporting/ExportFormat.cs`, add `Db` to the enum:

```csharp
public enum ExportFormat
{
    PlainText,
    HtmlDark,
    HtmlLight,
    Csv,
    Json,
    Db,
}
```

Add the arm to `GetFileExtension` (before the `_ =>` default):

```csharp
                ExportFormat.Db => "db",
```

Add the arm to `GetDisplayName` (before the `_ =>` default):

```csharp
                ExportFormat.Db => "SQLite",
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~ExportFormatSpecs"`
Expected: PASS (2/2).

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props DiscordChatExporter.Core/DiscordChatExporter.Core.csproj DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj DiscordChatExporter.Core/Exporting/ExportFormat.cs DiscordChatExporter.Cli.Tests/Specs/ExportFormatSpecs.cs
git commit -m "SQLite #2: add Microsoft.Data.Sqlite dep + ExportFormat.Db plumbing"
```

---

### Task 2: `SqliteMessageWriter` + wire into `CreateMessageWriter` (TDD)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/SqliteMessageWriter.cs`
- Modify: `DiscordChatExporter.Core/Exporting/MessageExporter.cs:138-158`
- Test: `DiscordChatExporter.Cli.Tests/Specs/SqliteMessageWriterSpecs.cs` (create)

This is the core task. The writer is driven token-free through a synthetic offline `ExportContext` — `new DiscordClient("fake-token")` never touches the network because the writer only stores reaction *counts* and `ResolveAssetUrlAsync` short-circuits when `ShouldDownloadAssets == false`.

- [ ] **Step 1: Write the failing unit test**

Create `DiscordChatExporter.Cli.Tests/Specs/SqliteMessageWriterSpecs.cs`:

```csharp
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
    private static ExportContext CreateContext(string outputPath)
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
            PartitionLimit.Null,
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
            command.CommandText =
                "SELECT content FROM messages WHERE id = '1001';";
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
            await writer.WriteMessageAsync(CreateMessage(4001, CreateUser(10, "alice"), "first run"));
            await writer.WritePostambleAsync();
        }

        // Second export over the same path.
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(CreateMessage(4002, CreateUser(11, "bob"), "second run"));
            await writer.WritePostambleAsync();
        }

        // Assert — only the second run's data is present.
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(1);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM messages;";
        ((string)command.ExecuteScalar()!).Should().Be("4002");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteMessageWriterSpecs"`
Expected: FAIL — `SqliteMessageWriter` does not exist (compile error).

- [ ] **Step 3: Implement the writer**

Create `DiscordChatExporter.Core/Exporting/SqliteMessageWriter.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Markdown.Parsing;
using Microsoft.Data.Sqlite;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

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
        """;

    private readonly string _databaseFilePath;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

    public SqliteMessageWriter(string databaseFilePath, ExportContext context)
        : base(Stream.Null, context)
    {
        _databaseFilePath = databaseFilePath;
    }

    private async ValueTask<string> FormatMarkdownAsync(
        string markdown,
        CancellationToken cancellationToken = default
    ) =>
        Context.Request.ShouldFormatMarkdown
            ? await PlainTextMarkdownVisitor.FormatAsync(Context, markdown, cancellationToken)
            : markdown;

    private string? NormalizeOrNull(DateTimeOffset? instant) =>
        instant is { } value
            ? Context.NormalizeDate(value).ToString("o", CultureInfo.InvariantCulture)
            : null;

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

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

        await WriteAuthorAsync(message.Author, cancellationToken);

        using (var command = _connection!.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO messages
                    (id, type, timestamp, timestamp_edited, call_ended_timestamp,
                     is_pinned, content, author_id, reference_message_id)
                VALUES
                    ($id, $type, $timestamp, $timestampEdited, $callEnded,
                     $isPinned, $content, $authorId, $referenceMessageId);
                """;
            AddParameter(command, "$id", message.Id.ToString());
            AddParameter(command, "$type", message.Kind.ToString());
            AddParameter(
                command,
                "$timestamp",
                Context.NormalizeDate(message.Timestamp).ToString("o", CultureInfo.InvariantCulture)
            );
            AddParameter(command, "$timestampEdited", NormalizeOrNull(message.EditedTimestamp));
            AddParameter(command, "$callEnded", NormalizeOrNull(message.CallEndedTimestamp));
            AddParameter(command, "$isPinned", message.IsPinned ? 1 : 0);
            AddParameter(command, "$content", content);
            AddParameter(command, "$authorId", message.Author.Id.ToString());
            AddParameter(
                command,
                "$referenceMessageId",
                message.Reference?.MessageId?.ToString()
            );
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var command = _connection!.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText =
                "INSERT INTO messages_fts (content, message_id) VALUES ($content, $messageId);";
            AddParameter(command, "$content", content);
            AddParameter(command, "$messageId", message.Id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var attachment in message.Attachments)
        {
            using var command = _connection!.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO attachments (message_id, id, url, file_name, file_size_bytes)
                VALUES ($messageId, $id, $url, $fileName, $fileSizeBytes);
                """;
            AddParameter(command, "$messageId", message.Id.ToString());
            AddParameter(command, "$id", attachment.Id.ToString());
            AddParameter(
                command,
                "$url",
                await Context.ResolveAssetUrlAsync(attachment.Url, cancellationToken)
            );
            AddParameter(command, "$fileName", attachment.FileName);
            AddParameter(command, "$fileSizeBytes", attachment.FileSize.TotalBytes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Reactions: counts only. We deliberately do NOT call GetMessageReactionsAsync
        // (the per-user fetch), keeping the writer network-free.
        foreach (var reaction in message.Reactions)
        {
            using var command = _connection!.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO reactions (message_id, emoji_id, emoji_name, emoji_code, is_animated, count)
                VALUES ($messageId, $emojiId, $emojiName, $emojiCode, $isAnimated, $count);
                """;
            AddParameter(command, "$messageId", message.Id.ToString());
            AddParameter(command, "$emojiId", reaction.Emoji.Id?.ToString());
            AddParameter(command, "$emojiName", reaction.Emoji.Name);
            AddParameter(command, "$emojiCode", reaction.Emoji.Code);
            AddParameter(command, "$isAnimated", reaction.Emoji.IsAnimated ? 1 : 0);
            AddParameter(command, "$count", reaction.Count);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async ValueTask WriteAuthorAsync(User user, CancellationToken cancellationToken)
    {
        using var command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        // OR IGNORE dedupes by the author id primary key.
        command.CommandText = """
            INSERT OR IGNORE INTO authors (id, name, discriminator, nickname, color, is_bot, avatar_url)
            VALUES ($id, $name, $discriminator, $nickname, $color, $isBot, $avatarUrl);
            """;
        AddParameter(command, "$id", user.Id.ToString());
        AddParameter(command, "$name", user.Name);
        AddParameter(command, "$discriminator", user.DiscriminatorFormatted);
        AddParameter(
            command,
            "$nickname",
            Context.TryGetMember(user.Id)?.DisplayName ?? user.DisplayName
        );
        AddParameter(command, "$color", Context.TryGetUserColor(user.Id)?.ToHexString());
        AddParameter(command, "$isBot", user.IsBot ? 1 : 0);
        AddParameter(
            command,
            "$avatarUrl",
            await Context.ResolveAssetUrlAsync(
                Context.TryGetMember(user.Id)?.AvatarUrl ?? user.AvatarUrl,
                cancellationToken
            )
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            command.CommandText = "UPDATE export_info SET message_count = $count;";
            AddParameter(command, "$count", MessagesWritten);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await _transaction.CommitAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        // If the postamble never ran (e.g. an exception mid-export), disposing the transaction
        // rolls it back; disposing the connection releases the file handle (Pooling=False).
        if (_transaction is not null)
            await _transaction.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();

        await base.DisposeAsync();
    }

    private static void DeleteDatabaseFiles(string databaseFilePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = databaseFilePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
```

> If the compiler reports `ToHexString` or `.Pipe`/`.ToDate` as missing, check the `using` directives at the top of `JsonMessageWriter.cs` (the reference model) and add the matching namespace. `ToHexString` is the same extension `JsonMessageWriter` uses on `Color`; `ToDate` is on `Snowflake`.

- [ ] **Step 4: Wire the `Db` arm into `CreateMessageWriter`**

In `DiscordChatExporter.Core/Exporting/MessageExporter.cs`, in `CreateMessageWriter` (around line 143), add this arm before the `_ =>` default. **It must NOT call `File.Create`** — SQLite opens and owns the file itself; pre-creating/locking it with a `FileStream` would conflict:

```csharp
            ExportFormat.Db => new SqliteMessageWriter(filePath, context),
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteMessageWriterSpecs"`
Expected: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/SqliteMessageWriter.cs DiscordChatExporter.Core/Exporting/MessageExporter.cs DiscordChatExporter.Cli.Tests/Specs/SqliteMessageWriterSpecs.cs
git commit -m "SQLite #2: SqliteMessageWriter + wire ExportFormat.Db into the export pipeline"
```

---

### Task 3: Token-gated integration spec (real Discord data)

**Files:**
- Modify: `DiscordChatExporter.Cli.Tests/Infra/ExportWrapper.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/SqliteContentSpecs.cs` (create)

This mirrors how every other format is tested (`JsonContentSpecs` etc.): it runs the full `ExportChannelsCommand` against a real test channel using `Secrets.DiscordToken`. It is **token-gated** — it only runs where a Discord token is configured (CI / the user's machine), not in the token-free dev sandbox. Its value is proving the real `Message`→DB mapping against live data and guarding the format in CI. The Task 2 unit tests are the offline regression gate.

- [ ] **Step 1: Add a `.db` export helper to `ExportWrapper`**

In `DiscordChatExporter.Cli.Tests/Infra/ExportWrapper.cs`, the existing private `ExportAsync` returns the file *text*, which is meaningless for a binary `.db`. Add a sibling that returns the file *path*. Add these members to the class:

```csharp
    private static async ValueTask<string> ExportToFileAsync(
        Snowflake channelId,
        ExportFormat format
    )
    {
        var fileName = channelId.ToString() + '.' + format.GetFileExtension();
        var filePath = Path.Combine(DirPath, fileName);

        using var _ = await Locker.LockAsync(filePath);
        using var console = new FakeConsole();

        if (!File.Exists(filePath))
        {
            await new ExportChannelsCommand
            {
                Token = Secrets.DiscordToken,
                ChannelIds = [channelId],
                ExportFormat = format,
                OutputPath = filePath,
                Locale = "en-US",
                IsUtcNormalizationEnabled = true,
            }.ExecuteAsync(console);
        }

        return filePath;
    }

    public static async ValueTask<string> ExportAsDbAsync(Snowflake channelId) =>
        await ExportToFileAsync(channelId, ExportFormat.Db);
```

- [ ] **Step 2: Write the token-gated content spec**

Create `DiscordChatExporter.Cli.Tests/Specs/SqliteContentSpecs.cs`. It reuses `ChannelIds.DateRangeTestCases` — the same real test channel `JsonContentSpecs` exports (`ChannelIds` lives in `DiscordChatExporter.Cli.Tests.Infra`, already imported):

```csharp
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteContentSpecs
{
    [Fact]
    public async Task I_can_export_a_channel_to_a_queryable_sqlite_database()
    {
        // Act
        var dbPath = await ExportWrapper.ExportAsDbAsync(ChannelIds.DateRangeTestCases);

        // Assert
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();

        long messageCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM messages;";
            messageCount = (long)command.ExecuteScalar()!;
        }

        messageCount.Should().BeGreaterThan(0);

        // export_info.message_count agrees with the messages table.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT message_count FROM export_info;";
            ((long)command.ExecuteScalar()!).Should().Be(messageCount);
        }

        // The FTS index is populated 1:1 with messages.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM messages_fts;";
            ((long)command.ExecuteScalar()!).Should().Be(messageCount);
        }
    }
}
```

- [ ] **Step 3: Verify it compiles (run is token-gated)**

Run: `dotnet build DiscordChatExporter.Cli.Tests`
Expected: build succeeds. (The test itself will be skipped/fail-without-token in the dev sandbox; it runs where `Secrets.DiscordToken` is set. Do not block on executing it here.)

> If `Secrets.DiscordToken` happens to be configured in this environment and you can run `dotnet test --filter "FullyQualifiedName~SqliteContentSpecs"` green, do so. Otherwise, confirming compilation is sufficient for this task; the offline Task 2 specs are the executable gate.

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Cli.Tests/Infra/ExportWrapper.cs DiscordChatExporter.Cli.Tests/Specs/SqliteContentSpecs.cs
git commit -m "SQLite #2: token-gated SqliteContentSpecs + ExportWrapper.ExportAsDbAsync"
```

---

### Task 4: Full build + trimmed-publish verification

**Files:** none created; this task verifies the whole feature builds, the offline tests pass, and the GUI still publishes as a working trimmed self-contained binary that ships the SQLite native engine.

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build DiscordChatExporter.slnx`
Expected: build succeeds, 0 errors.

- [ ] **Step 2: Run the full token-free test sweep**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteMessageWriterSpecs|FullyQualifiedName~ExportFormatSpecs|FullyQualifiedName~Continuation|FullyQualifiedName~Manifest"`
Expected: all pass (new SQLite + enum specs plus the existing manifest/continuation regression set). Adjust the filter to match the repo's existing token-free specs if names differ.

- [ ] **Step 3: Publish the GUI trimmed + self-contained and confirm the native engine ships**

Run (PowerShell, from the repo root):

```powershell
dotnet publish DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -o publish-verify
```

Then confirm the native asset is present and the app host launches:

```powershell
Get-ChildItem publish-verify -Filter 'e_sqlite3.dll'   # must list the file
```

Expected: `e_sqlite3.dll` is present in the publish output (trimming did not strip it — consistent with the pre-flight spike). Build/publish completes with 0 errors.

- [ ] **Step 4: Clean up the verification publish directory**

```powershell
Remove-Item -Recurse -Force publish-verify
```

- [ ] **Step 5 (no commit unless fixups were needed):** If Steps 1–3 required any code fix, commit it with a clear message; otherwise this task produces no commit.

---

## Notes for the executor

- **Do not** add indexes, `PRAGMA foreign_keys`, or WAL mode — out of scope for v1 and (for WAL) actively harmful to the single-file guarantee.
- **Do not** reintroduce a DTO/seam layer — the writer is tested directly via `InternalsVisibleTo`.
- The user-facing redeploy of the GUI exe to `outputs/DiscordChatExporter-user-copy/` is deferred until after #5 (the Library home view) lands, so the whole #1–#6 batch ships together. Task 4 only *verifies* the publish; it does not overwrite the user copy.
- After all tasks: dispatch a final whole-feature code review, then continue the build order to #5.
