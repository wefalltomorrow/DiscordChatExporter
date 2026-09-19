# SQLite (.db) Resume Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make `.db` (SQLite) exports resumable via the existing Continue-export feature.

**Architecture:** Add a `SqliteExportInspector` (reads the resume cutoff from the `.db`) and a `SqliteExportMerger` (ATTACHes the freshly-exported temp `.db` and copies the new rows in), then wire `.db` into `ContinuationFormat` and the GUI file picker. No writer/engine changes — the temp export is a fresh `.db`; the append is pure SQL in the merger.

**Tech Stack:** C# / .NET 10, Microsoft.Data.Sqlite, xUnit + FluentAssertions. Spec: `docs/specs/2026-06-05-sqlite-resume-design.md`.

**Build/test:** `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteContinuation"` (token-free). Repo has `TreatWarningsAsErrors=true` + CSharpier-on-build.

---

### Task 1: `SqliteExportInspector`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/SqliteExportInspector.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/SqliteContinuationSpecs.cs`

- [ ] **Step 1: Write failing tests** (create the test file with shared fixtures mirroring `SqliteMessageWriterSpecs`):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
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
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ExportContext CreateContext(string outputPath)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2), ChannelKind.GuildTextChat, new Snowflake(1), null,
            "test-channel", 0, null, "topic", false, null
        );
        var request = new ExportRequest(
            guild, channel, outputPath, null, ExportFormat.Db, null, null,
            PartitionLimit.Null, MessageFilter.Null,
            isReverseMessageOrder: false, shouldFormatMarkdown: false,
            shouldDownloadAssets: false, shouldReuseAssets: false,
            locale: "en-US", isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id), MessageKind.Default, MessageFlags.None, author,
            DateTimeOffset.UnixEpoch.AddSeconds(id), null, null, false, content,
            [], [], [], [], [], null, null, null, null
        );

    private async Task<string> WriteDbAsync(string fileName, params (ulong id, string content)[] messages)
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
        var path = await WriteDbAsync("empty.db"); // preamble+postamble, no messages

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.ExistingCount.Should().Be(0);
        cutoff.CutoffIsExact.Should().BeFalse();
    }

    [Fact]
    public async Task Inspector_rejects_a_non_sqlite_file()
    {
        var path = Path.Combine(_dir, "notadb.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }
}
```

- [ ] **Step 2: Run, verify it fails** — `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteContinuation"` → FAIL (SqliteExportInspector not found).

- [ ] **Step 3: Implement** `SqliteExportInspector.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Continuation;

// Note: InvalidExportException lives in this same namespace (Continuation), so no extra using.
public static class SqliteExportInspector
{
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
            string? lastIdText;
            await using (var msg = connection.CreateCommand())
            {
                msg.CommandText =
                    "SELECT COUNT(*), "
                    + "(SELECT id FROM messages ORDER BY CAST(id AS INTEGER) DESC LIMIT 1) "
                    + "FROM messages;";
                await using var reader = await msg.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                count = reader.GetInt64(0);
                lastIdText = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var channelId = new Snowflake(ulong.Parse(channelIdText, CultureInfo.InvariantCulture));
            var before = ParseSnowflakeDate(beforeText);

            Snowflake cutoff;
            bool exact;
            if (lastIdText is not null)
            {
                cutoff = new Snowflake(ulong.Parse(lastIdText, CultureInfo.InvariantCulture));
                exact = true;
            }
            else
            {
                // Empty export: resume from the original lower bound (or the beginning).
                cutoff = ParseSnowflakeDate(afterText) ?? new Snowflake(0);
                exact = false;
            }

            // A .db is an unordered row set merged by id, so resume is safe regardless of the
            // original export order (unlike the text formats).
            return new ContinuationCutoff(channelId, cutoff, before, true, count, exact);
        }
        catch (SqliteException ex)
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
    }

    private static Snowflake? ParseSnowflakeDate(string? text) =>
        !string.IsNullOrEmpty(text)
        && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var date
        )
            ? Snowflake.FromDate(date)
            : null;
}
```

- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Commit** — `git add` the two files; `git commit -m "SQLite resume: add SqliteExportInspector"`.

---

### Task 2: `SqliteExportMerger`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/SqliteExportMerger.cs`
- Test: append to `DiscordChatExporter.Cli.Tests/Specs/Continuation/SqliteContinuationSpecs.cs`

- [ ] **Step 1: Add failing tests** (append inside the class; reuse the `OpenReadOnly`/`Count` helpers — add them too):

```csharp
    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var c = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        c.Open();
        return c;
    }

    private static long Count(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Merger_appends_new_messages_and_updates_count_and_fts()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "old one"), (1002, "old two"), (1003, "old three"));
        var incoming = await WriteDbAsync("new.db", (1004, "fresh four"), (1005, "fresh five"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing); // Cutoff == 1003

        var total = await SqliteExportMerger.MergeAsync(existing, incoming, cutoff, DateTimeOffset.UnixEpoch);

        total.Should().Be(5);
        using var c = OpenReadOnly(existing);
        Count(c, "SELECT COUNT(*) FROM messages;").Should().Be(5);
        Count(c, "SELECT COUNT(*) FROM messages_fts;").Should().Be(5);
        Count(c, "SELECT message_count FROM export_info;").Should().Be(5);

        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'fresh';";
        ((long)q.ExecuteScalar()!).Should().Be(2);
    }

    [Fact]
    public async Task Merger_ignores_a_boundary_duplicate_message()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));
        // Incoming re-includes 1003 (the cutoff) plus genuinely new rows.
        var incoming = await WriteDbAsync("new.db", (1003, "c again"), (1004, "d"), (1005, "e"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing); // 1003

        var total = await SqliteExportMerger.MergeAsync(existing, incoming, cutoff, DateTimeOffset.UnixEpoch);

        total.Should().Be(5);
        using var c = OpenReadOnly(existing);
        Count(c, "SELECT COUNT(*) FROM messages;").Should().Be(5);
        // 1003 keeps its ORIGINAL content (the duplicate was not copied).
        using var q = c.CreateCommand();
        q.CommandText = "SELECT content FROM messages WHERE id = '1003';";
        ((string)q.ExecuteScalar()!).Should().Be("c");
    }
```

- [ ] **Step 2: Run, verify fail** (SqliteExportMerger not found).

- [ ] **Step 3: Implement** `SqliteExportMerger.cs`. Copy is filtered by `id > cutoff` so a boundary-duplicate message (and its dependent attachment/reaction/FTS rows) is never re-inserted — `attachments`/`reactions` have no primary key, so `OR IGNORE` alone wouldn't dedupe them:

```csharp
using System;
using System.Globalization;
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
    )
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = existingDatabaseFilePath,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(cancellationToken);

        // Single file, no -wal/-shm sidecars (matches the writer).
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

            await using (var copy = connection.CreateCommand())
            {
                copy.Transaction = transaction;
                // Authors dedupe by their id PK. Message-scoped rows are copied only when strictly
                // newer than the cutoff, so a boundary-duplicate message (== cutoff) and all of its
                // dependent rows are excluded — the new export is exclusive-after-cutoff anyway, so
                // this only ever drops genuine duplicates.
                copy.CommandText = """
                    INSERT OR IGNORE INTO main.authors SELECT * FROM incoming.authors;
                    INSERT OR IGNORE INTO main.messages
                        SELECT * FROM incoming.messages WHERE CAST(id AS INTEGER) > $cutoff;
                    INSERT INTO main.attachments
                        SELECT * FROM incoming.attachments WHERE CAST(message_id AS INTEGER) > $cutoff;
                    INSERT INTO main.reactions
                        SELECT * FROM incoming.reactions WHERE CAST(message_id AS INTEGER) > $cutoff;
                    INSERT INTO main.messages_fts (content, message_id)
                        SELECT content, message_id FROM incoming.messages_fts
                        WHERE CAST(message_id AS INTEGER) > $cutoff;
                    """;
                copy.Parameters.AddWithValue("$cutoff", (long)cutoff.Cutoff.Value);
                await copy.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE export_info SET message_count = (SELECT COUNT(*) FROM main.messages), "
                    + "exported_at = $now;";
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
            await using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE incoming;";
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM main.messages;";
            return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
    }
}
```

- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Commit** — `git commit -m "SQLite resume: add SqliteExportMerger (ATTACH + cutoff-filtered copy)"`.

---

### Task 3: Wire `.db` into `ContinuationFormat`

**Files:**
- Modify: `DiscordChatExporter.Core/Exporting/Continuation/ContinuationFormat.cs`

- [ ] **Step 1: Edit `IsSupportedExtension`** (line 12) — add `or ".db"`:

```csharp
    public static bool IsSupportedExtension(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant()
            is ".json" or ".html" or ".htm" or ".csv" or ".db";
```

- [ ] **Step 2: Edit `FormatFor`** — add the `.db` arm before the throw:

```csharp
            ".csv" => ExportFormat.Csv,
            ".db" => ExportFormat.Db,
```

- [ ] **Step 3: Edit `ReadCutoffAsync`** — add the `.db` arm before the throw:

```csharp
            ".csv" => await CsvExportInspector.InspectAsync(filePath, cancellationToken),
            ".db" => await SqliteExportInspector.InspectAsync(filePath, cancellationToken),
```

- [ ] **Step 4: Edit `MergeAsync`** — add the `.db` arm before the throw (the merger already returns the TOTAL, so no `+ ExistingCount` adjustment like CSV needs):

```csharp
            ".db" => await SqliteExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                cutoff,
                exportedAt,
                cancellationToken
            ),
```

- [ ] **Step 5: Build** — `dotnet build DiscordChatExporter.Core` → 0 errors.
- [ ] **Step 6: Commit** — `git commit -m "SQLite resume: wire .db into ContinuationFormat"`.

---

### Task 4: GUI file picker

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` (the `ContinueExportAsync` file picker, ~line 753)

- [ ] **Step 1: Edit the picker** to include `*.db` and rename the label:

```csharp
        var filePath = await _dialogManager.PromptSingleFilePathAsync(
            [
                new FilePickerFileType("Supported exports (JSON, HTML, CSV, SQLite)")
                {
                    Patterns = ["*.json", "*.html", "*.htm", "*.csv", "*.db"],
                },
            ]
        );
```

- [ ] **Step 2: Build the GUI** — `dotnet build DiscordChatExporter.Gui` → 0 warnings/0 errors. (`RefreshContinuedExportCatalogAsync`, added 2026-06-05, already refreshes the `.db`'s manifest entry + Library FTS index after a resume — no further change needed.)
- [ ] **Step 3: Commit** — `git commit -m "SQLite resume: allow .db in the Continue-export file picker"`.

---

### Task 5: Whole-feature verification

- [ ] **Step 1** — `dotnet build DiscordChatExporter.slnx` → 0/0.
- [ ] **Step 2** — `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteContinuation"` → all pass.
- [ ] **Step 3** — `dotnet test DiscordChatExporter.Gui.Tests` → 4/4 (no regression).
- [ ] **Step 4** — republish self-contained win-x64 and redeploy over the user-copy (preserve `Settings.dat`).
