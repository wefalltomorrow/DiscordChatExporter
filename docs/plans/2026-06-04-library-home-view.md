# Library Home View (#5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A first-class **Library** home view in the main window that catalogs past exports (from #1 manifests) and provides global full-text search across all SQLite (#2) exports.

**Architecture:** Two token-free Core units do the real work — `SqliteExportReader` (read-only FTS5 search over `.db` files) and `ExportCatalogBuilder` (aggregate `manifest.json` entries from tracked + scanned folders). The GUI adds: a persisted `KnownExportDirs` list (auto-recorded on each export), a view switch in `MainViewModel` (`CurrentPage` toggles Dashboard ↔ Library), and a thin `LibraryViewModel`/`LibraryView`. Design doc: `docs/specs/2026-06-04-library-home-view-design.md`.

**Tech Stack:** C# / .NET 10, Microsoft.Data.Sqlite (FTS5), Avalonia 12 + Material.Avalonia, CommunityToolkit.Mvvm, Cogwheel settings, xUnit + FluentAssertions.

**Decided scope (user, 2026-06-04):** browse + **global** search only (one box searches all `.db` exports, results labeled by source). **No** Continue/Re-run/verify in the Library; **no** integrity/SHA-256 check; **no** edited/deleted diff (that's #10). Discovery = auto-track exported folders **+** user-chosen folder scan.

**Conventions:** central package management (`Microsoft.Data.Sqlite` already referenced by Core + Cli.Tests from #2; `InternalsVisibleTo` Core→Cli.Tests already exists). Core search/catalog helpers are `public static` classes like the existing `ManifestReader`/`ExportSummarizer`. The token-free test project can construct everything (IVT in place). CSharpier runs on build.

---

## File Structure

**Create (Core):**
- `DiscordChatExporter.Core/Exporting/Library/SqliteSearchHit.cs` — result record.
- `DiscordChatExporter.Core/Exporting/Library/SqliteExportReader.cs` — `static`; FTS5 search over one or many `.db` files.
- `DiscordChatExporter.Core/Exporting/Library/ExportCatalogBuilder.cs` — `static`; aggregates manifest entries from directories + recursive folder scan.
- `DiscordChatExporter.Core/Exporting/Library/RecentExportDirs.cs` — `static`; pure dedup/cap helper for the tracked-folders list.

**Create (Tests):**
- `DiscordChatExporter.Cli.Tests/Specs/SqliteExportReaderSpecs.cs`
- `DiscordChatExporter.Cli.Tests/Specs/ExportCatalogBuilderSpecs.cs`
- `DiscordChatExporter.Cli.Tests/Specs/RecentExportDirsSpecs.cs`

**Create (GUI):**
- `DiscordChatExporter.Gui/ViewModels/Components/LibraryViewModel.cs`
- `DiscordChatExporter.Gui/Views/Components/LibraryView.axaml` (+ `.axaml.cs`)

**Modify (GUI):**
- `DiscordChatExporter.Gui/Services/SettingsService.cs` — add `KnownExportDirs`.
- `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` — auto-track export dir; `NavigateToLibrary` command + `LibraryRequested` event.
- `DiscordChatExporter.Gui/ViewModels/MainViewModel.cs` — `CurrentPage` + navigation.
- `DiscordChatExporter.Gui/Views/MainView.axaml` — bind ContentControl to `CurrentPage`.
- `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml` — add a "Library" header button.
- `DiscordChatExporter.Gui/Framework/ViewManager.cs` — add `LibraryViewModel => new LibraryView()`.
- `DiscordChatExporter.Gui/Framework/ViewModelManager.cs` — add `GetLibraryViewModel()`.
- `DiscordChatExporter.Gui/App.axaml.cs` — register `LibraryViewModel` (transient).
- `DiscordChatExporter.Gui/Localization/LocalizationManager.cs` + `.English.cs` — Library strings.

---

### Task 1: Core `SqliteExportReader` — FTS5 search (TDD, token-free)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Library/SqliteSearchHit.cs`
- Create: `DiscordChatExporter.Core/Exporting/Library/SqliteExportReader.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/SqliteExportReaderSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/SqliteExportReaderSpecs.cs`. It builds a real `.db` using the existing `SqliteMessageWriter` (driven via a synthetic offline `ExportContext` — same pattern as `SqliteMessageWriterSpecs.cs`, copy its `CreateContext`/`CreateUser`/`CreateMessage` helpers), then searches it.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteExportReaderSpecs : IDisposable
{
    private readonly string _dirPath = Path.Combine(
        Path.GetTempPath(),
        "DceLibTest_" + Guid.NewGuid().ToString("N")
    );

    public SqliteExportReaderSpecs() => Directory.CreateDirectory(_dirPath);

    public void Dispose()
    {
        try { Directory.Delete(_dirPath, true); } catch { }
    }

    // --- synthetic offline context + message builders (mirrors SqliteMessageWriterSpecs) ---
    private static ExportContext CreateContext(string outputPath)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(new Snowflake(2), ChannelKind.GuildTextChat, new Snowflake(1),
            null, "test-channel", 0, null, "topic", false, null);
        var request = new ExportRequest(guild, channel, outputPath, null, ExportFormat.Db,
            null, null, PartitionLimit.Null, MessageFilter.Null,
            isReverseMessageOrder: false, shouldFormatMarkdown: false, shouldDownloadAssets: false,
            shouldReuseAssets: false, locale: "en-US", isUtcNormalizationEnabled: true);
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(new Snowflake(id), MessageKind.Default, MessageFlags.None, author,
            DateTimeOffset.UnixEpoch, null, null, false, content,
            [], [], [], [], [], null, null, null, null);

    private async Task<string> WriteDbAsync(string fileName, params (ulong id, string content)[] messages)
    {
        var dbPath = Path.Combine(_dirPath, fileName);
        var author = CreateUser(10, "alice");
        await using var writer = new SqliteMessageWriter(dbPath, CreateContext(dbPath));
        await writer.WritePreambleAsync();
        foreach (var (id, content) in messages)
            await writer.WriteMessageAsync(CreateMessage(id, author, content));
        await writer.WritePostambleAsync();
        return dbPath;
    }

    [Fact]
    public async Task It_finds_messages_matching_a_term()
    {
        var db = await WriteDbAsync("a.db", (1, "the quick brown fox"), (2, "lazy dog sleeps"));

        var hits = await SqliteExportReader.SearchAsync(db, "fox", 50, default);

        hits.Should().ContainSingle();
        hits[0].MessageId.Should().Be("1");
        hits[0].AuthorName.Should().Be("alice");
        hits[0].DatabaseFilePath.Should().Be(db);
        hits[0].Snippet.Should().Contain("fox");
    }

    [Fact]
    public async Task Multi_word_queries_match_all_terms()
    {
        var db = await WriteDbAsync("a.db", (1, "quick brown fox"), (2, "quick lazy cat"));

        // Implicit AND of the two terms -> only message 1 has both.
        var hits = await SqliteExportReader.SearchAsync(db, "quick fox", 50, default);

        hits.Select(h => h.MessageId).Should().Equal("1");
    }

    [Fact]
    public async Task Special_characters_in_the_query_do_not_throw()
    {
        var db = await WriteDbAsync("a.db", (1, "hello world"));

        // FTS5 operator characters would break a raw MATCH; the reader must quote/escape.
        var act = async () => await SqliteExportReader.SearchAsync(db, "\"(foo* AND", 50, default);

        await act.Should().NotThrowAsync();
        (await SqliteExportReader.SearchAsync(db, "\"(foo* AND", 50, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_query_returns_no_hits()
    {
        var db = await WriteDbAsync("a.db", (1, "hello world"));

        (await SqliteExportReader.SearchAsync(db, "   ", 50, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAcross_aggregates_and_skips_bad_paths()
    {
        var db1 = await WriteDbAsync("a.db", (1, "alpha shared"));
        var db2 = await WriteDbAsync("b.db", (2, "beta shared"));
        var missing = Path.Combine(_dirPath, "does-not-exist.db");

        var hits = await SqliteExportReader.SearchAcrossAsync(
            [db1, missing, db2], "shared", 50, default);

        hits.Select(h => h.DatabaseFilePath).Should().BeEquivalentTo([db1, db2]);
        hits.Should().HaveCount(2);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteExportReaderSpecs"`
Expected: FAIL — `SqliteExportReader`/`SqliteSearchHit` don't exist (compile error).

- [ ] **Step 3: Implement `SqliteSearchHit`**

Create `DiscordChatExporter.Core/Exporting/Library/SqliteSearchHit.cs`:

```csharp
namespace DiscordChatExporter.Core.Exporting.Library;

// One full-text-search hit. DatabaseFilePath identifies the source .db so the GUI can label
// the hit by joining it back to the catalog entry for that file.
public sealed record SqliteSearchHit(
    string DatabaseFilePath,
    string MessageId,
    string Timestamp,
    string AuthorName,
    string Snippet
);
```

- [ ] **Step 4: Implement `SqliteExportReader`**

Create `DiscordChatExporter.Core/Exporting/Library/SqliteExportReader.cs`:

```csharp
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
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteExportReaderSpecs"`
Expected: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Library/SqliteSearchHit.cs DiscordChatExporter.Core/Exporting/Library/SqliteExportReader.cs DiscordChatExporter.Cli.Tests/Specs/SqliteExportReaderSpecs.cs
git commit -m "Library #5: Core SqliteExportReader (FTS5 search over .db exports)"
```

---

### Task 2: Core `ExportCatalogBuilder` + `RecentExportDirs` (TDD, token-free)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Library/RecentExportDirs.cs`
- Create: `DiscordChatExporter.Core/Exporting/Library/ExportCatalogBuilder.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/RecentExportDirsSpecs.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/ExportCatalogBuilderSpecs.cs`

- [ ] **Step 1: Write the failing `RecentExportDirs` test**

Create `DiscordChatExporter.Cli.Tests/Specs/RecentExportDirsSpecs.cs`:

```csharp
using DiscordChatExporter.Core.Exporting.Library;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class RecentExportDirsSpecs
{
    [Fact]
    public void Adds_a_new_dir_to_the_front()
    {
        RecentExportDirs.Add(["b", "c"], "a", 10).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Moves_an_existing_dir_to_the_front_without_duplicating()
    {
        RecentExportDirs.Add(["a", "b", "c"], "c", 10).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void Is_case_insensitive_on_paths()
    {
        RecentExportDirs.Add(["A", "b"], "a", 10).Should().Equal("a", "b");
    }

    [Fact]
    public void Caps_the_list_at_the_max()
    {
        RecentExportDirs.Add(["b", "c", "d"], "a", 3).Should().Equal("a", "b", "c");
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test ... --filter "FullyQualifiedName~RecentExportDirsSpecs"` → FAIL (no `RecentExportDirs`).

- [ ] **Step 3: Implement `RecentExportDirs`**

Create `DiscordChatExporter.Core/Exporting/Library/RecentExportDirs.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting.Library;

// Pure helper for the persisted most-recently-used export-folder list. Adds a directory to the
// front, removes any case-insensitive duplicate, and caps the length.
public static class RecentExportDirs
{
    public static IReadOnlyList<string> Add(
        IReadOnlyList<string> existing,
        string newDir,
        int max
    )
    {
        var result = new List<string> { newDir };
        result.AddRange(
            existing.Where(d => !string.Equals(d, newDir, StringComparison.OrdinalIgnoreCase))
        );
        return result.Take(max).ToArray();
    }
}
```

- [ ] **Step 4: Run to verify it passes** — expect 4/4.

- [ ] **Step 5: Write the failing `ExportCatalogBuilder` test**

Create `DiscordChatExporter.Cli.Tests/Specs/ExportCatalogBuilderSpecs.cs`. It writes real `manifest.json` files using the existing `ManifestWriter`, then aggregates them.

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportCatalogBuilderSpecs : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DceCatTest_" + Guid.NewGuid().ToString("N"));

    public ExportCatalogBuilderSpecs() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private async Task<string> WriteManifestAsync(string subDir, string file, string channelName)
    {
        var dir = Path.Combine(_root, subDir);
        Directory.CreateDirectory(dir);
        var entry = new ManifestEntry(
            "1", "Guild", "2", channelName, null,
            Path.Combine(dir, file), "json", 5,
            null, null, null, null, null, 100, "sha", false, DateTimeOffset.UnixEpoch);
        await ManifestWriter.WriteAsync(dir, [entry], DateTimeOffset.UnixEpoch);
        return dir;
    }

    [Fact]
    public async Task Aggregates_entries_from_multiple_directories()
    {
        var d1 = await WriteManifestAsync("one", "x.json", "alpha");
        var d2 = await WriteManifestAsync("two", "y.json", "beta");

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, d2]);

        catalog.Select(e => e.ChannelName).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task Dedupes_entries_by_file_path()
    {
        var d1 = await WriteManifestAsync("one", "x.json", "alpha");

        // Same directory listed twice -> entry must appear once.
        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, d1]);

        catalog.Should().ContainSingle();
    }

    [Fact]
    public async Task Tolerates_missing_or_manifestless_directories()
    {
        var d1 = await WriteManifestAsync("one", "x.json", "alpha");
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var missing = Path.Combine(_root, "ghost");

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, empty, missing]);

        catalog.Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_finds_manifests_recursively_under_a_root()
    {
        await WriteManifestAsync("nested/deep", "x.json", "alpha");

        var dirs = await ExportCatalogBuilder.ScanForExportDirsAsync(_root);

        dirs.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_root, "nested", "deep"));
    }
}
```

- [ ] **Step 6: Run to verify it fails** — FAIL (no `ExportCatalogBuilder`).

- [ ] **Step 7: Implement `ExportCatalogBuilder`**

Create `DiscordChatExporter.Core/Exporting/Library/ExportCatalogBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;

namespace DiscordChatExporter.Core.Exporting.Library;

// Builds the export catalog by reading manifest.json from a set of directories and flattening
// their entries, de-duped by file path. Also scans a root folder recursively for manifests.
public static class ExportCatalogBuilder
{
    public static async ValueTask<IReadOnlyList<ManifestEntry>> BuildFromDirectoriesAsync(
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default
    )
    {
        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(dir, ExportManifest.FileName);
            var manifest = await ManifestReader.TryReadAsync(manifestPath, cancellationToken);
            if (manifest is null)
                continue;

            foreach (var entry in manifest.Entries)
                byFile[entry.File] = entry;
        }

        return byFile
            .Values.OrderByDescending(e => e.ExportedAt)
            .ToArray();
    }

    // Returns the directories under rootDir (inclusive) that contain a manifest.json.
    public static ValueTask<IReadOnlyList<string>> ScanForExportDirsAsync(
        string rootDir,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(rootDir))
            return new ValueTask<IReadOnlyList<string>>([]);

        try
        {
            var dirs = Directory
                .EnumerateFiles(rootDir, ExportManifest.FileName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ValueTask<IReadOnlyList<string>>(dirs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValueTask<IReadOnlyList<string>>([]);
        }
    }
}
```

- [ ] **Step 8: Run to verify it passes** — `dotnet test ... --filter "FullyQualifiedName~ExportCatalogBuilderSpecs|FullyQualifiedName~RecentExportDirsSpecs"` → expect 8/8.

- [ ] **Step 9: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Library/RecentExportDirs.cs DiscordChatExporter.Core/Exporting/Library/ExportCatalogBuilder.cs DiscordChatExporter.Cli.Tests/Specs/RecentExportDirsSpecs.cs DiscordChatExporter.Cli.Tests/Specs/ExportCatalogBuilderSpecs.cs
git commit -m "Library #5: Core ExportCatalogBuilder + RecentExportDirs helper"
```

---

### Task 3: Settings `KnownExportDirs` + auto-track on export

**Files:**
- Modify: `DiscordChatExporter.Gui/Services/SettingsService.cs`
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`

This is GUI plumbing (no token-free unit test — the pure list logic is already covered by `RecentExportDirsSpecs`). Keep the change minimal.

- [ ] **Step 1: Add the persisted property**

In `DiscordChatExporter.Gui/Services/SettingsService.cs`, add alongside the other `Last*` properties (follow the exact existing `[ObservableProperty]` style; the source-gen `SerializerContext` already covers the whole `SettingsService`, so a `string[]` persists with no extra work):

```csharp
    [ObservableProperty]
    public partial string[] KnownExportDirs { get; set; } = [];
```

- [ ] **Step 2: Record each successful export's directory**

In `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`, inside `RunExportCoreAsync`, after a channel exports successfully (the block around `Interlocked.Increment(ref successfulExportCount);`), capture the request's output directory. Because the loop is parallel, collect into a thread-safe set and apply once after the loop. Concretely:

- Before the parallel loop, add a local: `var exportedDirs = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);`
- In the success path, add: `exportedDirs.TryAdd(request.OutputDirPath, 0);`
- After the loop (near where the summary snackbar is shown, when `successfulExportCount > 0`), fold them into settings and save:

```csharp
            foreach (var dir in exportedDirs.Keys)
                _settingsService.KnownExportDirs =
                    RecentExportDirs.Add(_settingsService.KnownExportDirs, dir, 50).ToArray();

            _settingsService.Save();
```

Add `using DiscordChatExporter.Core.Exporting.Library;` to the file. (`RecentExportDirs.Add` returns `IReadOnlyList<string>`; `.ToArray()` matches the `string[]` property type.)

- [ ] **Step 3: Build + regression**

Run: `dotnet build DiscordChatExporter.slnx -c Release` → 0 errors.
Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~RecentExportDirs|FullyQualifiedName~Manifest"` → all pass (no regression).

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Gui/Services/SettingsService.cs DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs
git commit -m "Library #5: persist KnownExportDirs; auto-track export folders"
```

---

### Task 4: Navigation infrastructure (Dashboard ↔ Library view switch)

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/MainViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/MainView.axaml`
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml`
- Modify: `DiscordChatExporter.Gui/Framework/ViewManager.cs`
- Modify: `DiscordChatExporter.Gui/Framework/ViewModelManager.cs`
- Modify: `DiscordChatExporter.Gui/App.axaml.cs`

This wires the switch but the Library view itself is built in Task 5. To keep this task independently buildable, create a **minimal placeholder** `LibraryViewModel`/`LibraryView` here (empty shell) and flesh it out in Task 5. (Alternatively do Task 5 first; but this ordering keeps the structural change isolated.) The shell must inherit the right base types so `ViewManager` resolves it.

- [ ] **Step 1: Create the placeholder `LibraryViewModel`**

Create `DiscordChatExporter.Gui/ViewModels/Components/LibraryViewModel.cs`. Mirror the class/namespace shape of `DashboardViewModel` (inherit `ViewModelBase`; it's in `DiscordChatExporter.Gui.ViewModels.Components`). Minimal shell:

```csharp
using System;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class LibraryViewModel(LocalizationManager localizationManager) : ViewModelBase
{
    public LocalizationManager LocalizationManager { get; } = localizationManager;

    // Raised when the user wants to return to the Dashboard.
    public event EventHandler? BackRequested;

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);
}
```

> Confirm `ViewModelBase`'s namespace/constructor by opening `DashboardViewModel.cs` and `MainViewModel.cs`; match exactly (if `ViewModelBase` is in `DiscordChatExporter.Gui.Framework`, the `using` above is correct). If `ViewModelBase` has an `InitializeAsync` you should override, leave it for Task 5.

- [ ] **Step 2: Create the placeholder `LibraryView`**

Create `DiscordChatExporter.Gui/Views/Components/LibraryView.axaml` + `.axaml.cs`, mirroring `DashboardView.axaml.cs`'s code-behind exactly (same base class — likely `UserControl<LibraryViewModel>` per the repo's `Framework` base; copy `DashboardView.axaml.cs` and swap the type). Minimal XAML:

```xml
<UserControl
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:components="clr-namespace:DiscordChatExporter.Gui.ViewModels.Components"
    x:Class="DiscordChatExporter.Gui.Views.Components.LibraryView"
    x:DataType="components:LibraryViewModel">
    <DockPanel>
        <TextBlock DockPanel.Dock="Top" Margin="16" Text="Library" />
    </DockPanel>
</UserControl>
```

> Match the exact code-behind base type used by `DashboardView.axaml.cs` (open it). The `x:Class` and `x:DataType` must line up with the new VM.

- [ ] **Step 3: Register `LibraryViewModel` in DI**

In `DiscordChatExporter.Gui/App.axaml.cs`, after the `DashboardViewModel` registration (line ~49):

```csharp
        services.AddTransient<LibraryViewModel>();
```

Add the `using DiscordChatExporter.Gui.ViewModels.Components;` if not already present.

- [ ] **Step 4: Add `GetLibraryViewModel()` to `ViewModelManager`**

In `DiscordChatExporter.Gui/Framework/ViewModelManager.cs`:

```csharp
    public LibraryViewModel GetLibraryViewModel() =>
        services.GetRequiredService<LibraryViewModel>();
```

- [ ] **Step 5: Add the `ViewManager` case**

In `DiscordChatExporter.Gui/Framework/ViewManager.cs`, in the `TryCreateView` switch, add (before `_ => null`):

```csharp
            LibraryViewModel => new LibraryView(),
```

Add `using`s for the VM (`...ViewModels.Components`) and View (`...Views.Components`) namespaces if needed.

- [ ] **Step 6: `MainViewModel` — `CurrentPage` + navigation**

In `DiscordChatExporter.Gui/ViewModels/MainViewModel.cs`:
- Replace the read-only `Dashboard` property (line ~22) with a kept reference plus an observable current page:

```csharp
    public DashboardViewModel Dashboard { get; } = viewModelManager.GetDashboardViewModel();

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }
```

- In `InitializeAsync` (line ~99), set the default page and wire the Dashboard → Library navigation once:

```csharp
        CurrentPage = Dashboard;
        Dashboard.LibraryRequested += (_, _) => ShowLibrary();
```

- Add the navigation methods:

```csharp
    private void ShowLibrary()
    {
        var library = viewModelManager.GetLibraryViewModel();
        library.BackRequested += (_, _) => CurrentPage = Dashboard;
        CurrentPage = library;
    }
```

> `viewModelManager` is the primary-constructor parameter already in scope. `ViewModelBase` is the shared base of both VMs. A fresh `LibraryViewModel` per navigation means the catalog reloads each time the user opens it (desired).

- [ ] **Step 7: `MainView.axaml` — bind to `CurrentPage`**

In `DiscordChatExporter.Gui/Views/MainView.axaml`, change the content binding:

```xml
            <ContentControl Content="{Binding CurrentPage}" />
```

(was `{Binding Dashboard}`).

- [ ] **Step 8: `DashboardViewModel` — `LibraryRequested` event + command**

In `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`:

```csharp
    // Raised when the user opens the Library from the Dashboard.
    public event EventHandler? LibraryRequested;

    [RelayCommand]
    private void NavigateToLibrary() => LibraryRequested?.Invoke(this, EventArgs.Empty);
```

(Add `using System;` if not present.)

- [ ] **Step 9: `DashboardView.axaml` — Library button**

In the header `Grid` (the one with the Settings button, around lines 58-72), widen the column definitions to add one more `Auto` column and add a button mirroring the Settings button's style, bound to `NavigateToLibraryCommand`, e.g. `<materialIcons:MaterialIcon Kind="ViewListOutline" />` (or another available Material icon). Place it next to Settings. Match the existing button's `Padding`/`Margin`/theme attributes exactly.

- [ ] **Step 10: Build + verify the switch**

Run: `dotnet build DiscordChatExporter.slnx -c Release` → 0 errors.

OPTIONAL but recommended headless test (the one load-bearing GUI binding): in `DiscordChatExporter.Gui.Tests`, add a fact that constructs `MainViewModel` (or drives the commands) and asserts `CurrentPage` is the Dashboard initially, becomes a `LibraryViewModel` after `Dashboard.NavigateToLibraryCommand.Execute(null)`, and returns to the Dashboard after the library's `NavigateBackCommand`. Follow the existing `TreeViewSelectionBindingTests` harness conventions. If wiring a full `MainViewModel` in the headless harness proves heavy, assert the event→navigation logic at the VM level instead. Report whichever you did.

- [ ] **Step 11: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/MainViewModel.cs DiscordChatExporter.Gui/Views/MainView.axaml DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Views/Components/DashboardView.axaml DiscordChatExporter.Gui/Framework/ViewManager.cs DiscordChatExporter.Gui/Framework/ViewModelManager.cs DiscordChatExporter.Gui/App.axaml.cs DiscordChatExporter.Gui/ViewModels/Components/LibraryViewModel.cs DiscordChatExporter.Gui/Views/Components/LibraryView.axaml DiscordChatExporter.Gui/Views/Components/LibraryView.axaml.cs
git commit -m "Library #5: Dashboard<->Library navigation infrastructure + shell view"
```

---

### Task 5: Library view — catalog list, folder scan, global search

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/LibraryViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/Components/LibraryView.axaml`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs` + `.English.cs`

Flesh out the shell from Task 4 into the real Library. The VM needs `SettingsService` (for `KnownExportDirs`), `DialogManager` (folder picker — `PromptDirectoryPathAsync()`), and `LocalizationManager`. It uses the Core `ExportCatalogBuilder` + `SqliteExportReader`.

- [ ] **Step 1: Implement `LibraryViewModel`**

Replace `LibraryViewModel.cs` with the full implementation:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class LibraryViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DialogManager _dialogManager;

    public LibraryViewModel(
        SettingsService settingsService,
        DialogManager dialogManager,
        LocalizationManager localizationManager
    )
    {
        _settingsService = settingsService;
        _dialogManager = dialogManager;
        LocalizationManager = localizationManager;
    }

    public LocalizationManager LocalizationManager { get; }

    public event EventHandler? BackRequested;

    public ObservableCollection<ManifestEntry> Entries { get; } = [];

    public ObservableCollection<LibrarySearchResult> SearchResults { get; } = [];

    [ObservableProperty]
    public partial string? SearchQuery { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool HasSearchableExports { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public override async Task InitializeAsync()
    {
        await ReloadAsync(_settingsService.KnownExportDirs);
    }

    private async Task ReloadAsync(IReadOnlyList<string> directories)
    {
        IsBusy = true;
        try
        {
            var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync(directories);

            Entries.Clear();
            foreach (var entry in catalog)
                Entries.Add(entry);

            HasSearchableExports = Entries.Any(IsSqlite);
            SearchResults.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var root = await _dialogManager.PromptDirectoryPathAsync();
        if (string.IsNullOrWhiteSpace(root))
            return;

        var found = await ExportCatalogBuilder.ScanForExportDirsAsync(root);

        // Merge scanned dirs into the persisted list (most-recent-first), then reload.
        foreach (var dir in found)
            _settingsService.KnownExportDirs =
                RecentExportDirs.Add(_settingsService.KnownExportDirs, dir, 200).ToArray();
        _settingsService.Save();

        await ReloadAsync(_settingsService.KnownExportDirs);
    }

    private bool CanSearch() => HasSearchableExports;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        SearchResults.Clear();

        var query = SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var dbPaths = Entries.Where(IsSqlite).Select(e => e.File).ToArray();

        IsBusy = true;
        try
        {
            var hits = await SqliteExportReader.SearchAcrossAsync(dbPaths, query, 200);

            // Label each hit by joining its source db path back to the catalog entry.
            var byFile = Entries.ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase);
            foreach (var hit in hits)
            {
                byFile.TryGetValue(hit.DatabaseFilePath, out var source);
                SearchResults.Add(new LibrarySearchResult(hit, source));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsSqlite(ManifestEntry entry) =>
        string.Equals(entry.Format, "db", StringComparison.OrdinalIgnoreCase)
        || entry.File.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
}

// A search hit paired with the catalog entry of the export it came from (for display labels).
public sealed record LibrarySearchResult(SqliteSearchHit Hit, ManifestEntry? Source)
{
    public string SourceLabel =>
        Source is not null ? $"{Source.GuildName} / {Source.ChannelName}" : Hit.DatabaseFilePath;
}
```

> Verify `ViewModelBase.InitializeAsync` is `virtual`/overridable and that `DialogManager.PromptDirectoryPathAsync()` has this exact signature (it is used in `ExportSetupViewModel.ShowOutputPathPromptAsync`). If `ViewModelBase` has no `InitializeAsync`, instead trigger `ReloadAsync` from the constructor via a fire-and-forget guarded call, or whatever pattern `DashboardViewModel` uses for initial load — match the codebase.

- [ ] **Step 2: Implement `LibraryView.axaml`**

Replace the placeholder XAML with the real view: a header (title, Back button → `NavigateBackCommand`, "Scan folder" button → `ScanFolderCommand`, a search `TextBox` bound to `SearchQuery` with its submit bound to `SearchCommand`), the catalog `ListBox` bound to `Entries`, and a results `ListBox` bound to `SearchResults`. Mirror `DashboardView.axaml`'s `ListBox` + `materialStyles:Card` item-template style. Use `LocalizationManager.*` for all user-facing strings. Bind the search box enabled-state to `HasSearchableExports`. For each `Entries` row show `GuildName` · `ChannelName` · `Format` · `MessageCount` · `ExportedAt` · `File`. For each `SearchResults` row show `SourceLabel` · `Hit.AuthorName` · `Hit.Timestamp` · `Hit.Snippet`.

(Exact XAML is left to the implementer to match the app's Material style; keep it consistent with `DashboardView.axaml`. No new control libraries — `ListBox` + `Card` + `TextBlock` + `Button` + `TextBox` only.)

- [ ] **Step 3: Localization strings**

Add to `LocalizationManager.English.cs` dictionary and as `=> Get();` properties in `LocalizationManager.cs` (follow the existing pattern exactly): `LibraryTitle` ("Library"), `LibraryBackButtonText` ("Back"), `LibraryScanFolderButtonText` ("Scan folder…"), `LibrarySearchPlaceholder` ("Search SQLite exports…"), `LibraryNoExportsMessage` ("No exports catalogued yet."), `LibrarySearchUnavailableMessage` ("Search requires a SQLite (.db) export."), `LibraryEmptyResultsMessage` ("No matching messages.").

- [ ] **Step 4: Build + regression + run the view**

Run: `dotnet build DiscordChatExporter.slnx -c Release` → 0 errors.
Run: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~SqliteExportReaderSpecs|FullyQualifiedName~ExportCatalogBuilderSpecs|FullyQualifiedName~RecentExportDirsSpecs"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/LibraryViewModel.cs DiscordChatExporter.Gui/Views/Components/LibraryView.axaml DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Library #5: catalog list, folder scan, and global FTS search UI"
```

---

### Task 6: Full build + trimmed-publish verification

- [ ] **Step 1:** `dotnet build DiscordChatExporter.slnx -c Release` → 0 errors.
- [ ] **Step 2:** Full token-free sweep: `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~Library|FullyQualifiedName~Sqlite|FullyQualifiedName~ExportFormatSpecs|FullyQualifiedName~Manifest|FullyQualifiedName~Continuation|FullyQualifiedName~Eta|FullyQualifiedName~RecentExportDirs|FullyQualifiedName~ExportCatalogBuilder"` → all pass. (Also run the Gui.Tests project if a headless nav test was added.)
- [ ] **Step 3:** Trimmed publish (the real profile — confirms no new first-party IL2026 from the new Core/GUI code and that SQLite still ships):

```powershell
dotnet publish DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -o publish-verify
Get-ChildItem publish-verify -Filter 'e_sqlite3.dll'   # must exist
```
Expected: build succeeds, `: error` count 0, `e_sqlite3.dll` present. (Pre-existing `Material.Avalonia` IL2035/IL2104 warnings are fine.)
- [ ] **Step 4:** `Remove-Item -Recurse -Force publish-verify`.
- [ ] **Step 5:** No commit unless a fixup was needed.

---

## Notes for the executor
- The Core search/catalog logic carries the real risk and is fully token-free tested (Tasks 1–2). The GUI tasks (3–5) mirror existing patterns; lean on the Explore-mapped conventions (manual `ViewManager` switch, `ViewModelManager` factory, Cogwheel settings, `ListBox`+`Card` style, `LocalizationManager` strings).
- Do NOT add Continue/Re-run/verify actions to the Library (explicitly out of scope), nor any integrity/SHA check, nor a global control library (no DataGrid).
- After all tasks: final whole-feature review, then the user-facing redeploy of the GUI exe to `outputs/DiscordChatExporter-user-copy/` (deferred to here — the end of the #1–#6 batch).
