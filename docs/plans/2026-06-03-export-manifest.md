# Export Manifest / Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After an export, write a `manifest.json` in the output directory that catalogs each output file (guild/channel identity, format, message count, first/last message id+timestamp, asset count, size, sha256) — the foundation that the Library view (#5) and resilient batch (#3) will read.

**Architecture:** `ChannelExporter.ExportChannelAsync` starts returning an `ExportResult` (the only thing that knows per-file message stats + asset count). The GUI export loop collects results across all channels, then — once, after the parallel loop — builds manifest entries and writes/merges `manifest.json` into each output directory. The manifest is a **list of self-describing entries, one per output file** (not per-channel, not single-guild): the same channel can produce several files (date-ranged re-exports, partitions) and a watched folder can mix guilds. Per-channel is a *view* derived by grouping on `channelId`.

**Tech Stack:** C# / .NET 10, System.Text.Json, SHA256 (`System.Security.Cryptography`). Tests: xUnit + FluentAssertions in `DiscordChatExporter.Cli.Tests` (token-free).

**Design decisions (locked):**
- **Grain:** one entry per output file. Partitioned export → N entries, each with its own per-file stats (clean, because `MessageExporter` owns partitioning) and a shared `partitioned: true` flag.
- **Write cadence:** collect into a `ConcurrentBag`, write **once** after `Parallel.ForEachAsync` returns. No incremental upsert/locking here — #3 adds that additively.
- **Where:** `<OutputDirPath>/manifest.json` for every export (single-channel and whole-server). Merge by filename with any existing manifest. Atomic temp + `File.Replace` + `.bak`, mirroring the continuation mergers.
- **Best-effort:** a manifest-write failure must never fail the export — catch, notify, move on.
- **Asset count** is best-effort (count of resolved asset URLs); `null` in the entry when asset download was off.
- **Out of scope for #1:** CLI manifest writing (GUI is what the user runs; add in a later parity pass), and updating the manifest on Continue-export.
- **Snowflake/timestamp:** track `message.Id` (Snowflake) and `message.Timestamp` (DateTimeOffset) per file; serialize ids as their decimal string (`Snowflake.ToString()`), timestamps as ISO-8601.

---

### Task 1: Expose the downloaded-asset count

**Files:**
- Modify: `DiscordChatExporter.Core/Exporting/ExportAssetDownloader.cs`
- Modify: `DiscordChatExporter.Core/Exporting/ExportContext.cs`

- [ ] **Step 1: Add the count property to the downloader**

In `ExportAssetDownloader.cs`, inside the first `internal partial class ExportAssetDownloader(...)` body, after the `_previousPathsByUrl` field, add:

```csharp
    // Number of distinct asset URLs resolved during this export (downloaded or reused).
    // Best-effort metric for the export manifest.
    public int DownloadedAssetCount => _previousPathsByUrl.Count;
```

- [ ] **Step 2: Surface it on the context**

In `ExportContext.cs`, after the `Request` property (around line 29), add:

```csharp
    public int DownloadedAssetCount => _assetDownloader.DownloadedAssetCount;
```

- [ ] **Step 3: Build Core**

Run: `dotnet build DiscordChatExporter.Core/DiscordChatExporter.Core.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/ExportAssetDownloader.cs DiscordChatExporter.Core/Exporting/ExportContext.cs
git commit -m "Manifest #1: expose DownloadedAssetCount on asset downloader + context"
```

---

### Task 2: `ExportResult` + per-file stats + `ChannelExporter` returns it

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/ExportResult.cs`
- Modify: `DiscordChatExporter.Core/Exporting/MessageExporter.cs`
- Modify: `DiscordChatExporter.Core/Exporting/ChannelExporter.cs`

- [ ] **Step 1: Create the result records**

Create `DiscordChatExporter.Core/Exporting/ExportResult.cs`:

```csharp
using System;
using System.Collections.Generic;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

// Stats for a single output file produced by an export (one channel may produce
// several files when partitioning is enabled).
public sealed record ExportedFile(
    string FilePath,
    long MessageCount,
    Snowflake? FirstMessageId,
    DateTimeOffset? FirstMessageTimestamp,
    Snowflake? LastMessageId,
    DateTimeOffset? LastMessageTimestamp
);

// The outcome of exporting one channel.
public sealed record ExportResult(
    IReadOnlyList<ExportedFile> Files,
    long MessageCount,
    int AssetCount
);
```

- [ ] **Step 2: Track per-file stats in `MessageExporter`**

In `MessageExporter.cs`, replace the field declarations + `MessagesExported` (lines 11-14) and the `InitializeWriterAsync`/`ExportMessageAsync` bodies as follows. First, add `using` lines at top if missing: `using System.Collections.Generic;` and `using System.Linq;`.

Replace the fields/property block:

```csharp
    private int _partitionIndex;
    private MessageWriter? _writer;

    private readonly List<MutableFileStats> _files = [];
    private MutableFileStats? _currentFile;

    public long MessagesExported { get; private set; }

    // Per-file stats captured during the export, in creation order.
    public IReadOnlyList<ExportedFile> Files =>
        _files.Select(f => f.ToExportedFile()).ToArray();
```

In `InitializeWriterAsync`, after the line `var writer = CreateMessageWriter(filePath, context.Request.Format, context);` and its `await writer.WritePreambleAsync(...)`, before `return _writer = writer;`, add:

```csharp
        _currentFile = new MutableFileStats(filePath);
        _files.Add(_currentFile);
```

In `ExportMessageAsync`, after `await writer.WriteMessageAsync(message, cancellationToken);` add `_currentFile!.Record(message);` so it reads:

```csharp
    public async ValueTask ExportMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        var writer = await InitializeWriterAsync(cancellationToken);
        await writer.WriteMessageAsync(message, cancellationToken);
        _currentFile!.Record(message);
        MessagesExported++;
    }
```

Then add a nested helper class at the end of the first `internal partial class MessageExporter` body (before the closing brace of that partial, i.e. right after `DisposeAsync`):

```csharp
    private sealed class MutableFileStats(string filePath)
    {
        private long _count;
        private Snowflake? _firstId;
        private DateTimeOffset? _firstTs;
        private Snowflake? _lastId;
        private DateTimeOffset? _lastTs;

        public void Record(Message message)
        {
            if (_count == 0)
            {
                _firstId = message.Id;
                _firstTs = message.Timestamp;
            }

            _lastId = message.Id;
            _lastTs = message.Timestamp;
            _count++;
        }

        public ExportedFile ToExportedFile() =>
            new(filePath, _count, _firstId, _firstTs, _lastId, _lastTs);
    }
```

Add `using DiscordChatExporter.Core.Discord;` at the top of `MessageExporter.cs` (for `Snowflake`).

- [ ] **Step 3: Make `ChannelExporter.ExportChannelAsync` return `ExportResult`**

In `ChannelExporter.cs`, change the signature from `public async ValueTask ExportChannelAsync(` to `public async ValueTask<ExportResult> ExportChannelAsync(`.

Replace the `await using var messageExporter = new MessageExporter(context);` line and everything after it with an explicit try/finally so the result is read **after** disposal (disposal forces an empty file for the filtered-to-empty case, and we want that file in `Files`):

```csharp
        // Initialize the exporter before further checks to ensure the file is created even if
        // an exception is thrown after this point.
        var messageExporter = new MessageExporter(context);
        try
        {
            // Check if the channel is empty
            if (request.Channel.IsEmpty)
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages; an empty file will be created."
                );
            }

            // Check if the 'before' and 'after' boundaries are valid
            if (
                (
                    request.Before is not null
                    && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
                )
                || (
                    request.After is not null
                    && !request.Channel.MayHaveMessagesAfter(request.After.Value)
                )
            )
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages within the specified period; an empty file will be created."
                );
            }

            var messages = !request.IsReverseMessageOrder
                ? discord.GetMessagesAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progress,
                    cancellationToken
                )
                : discord.GetMessagesInReverseAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progress,
                    cancellationToken
                );

            await foreach (var message in messages)
            {
                try
                {
                    // Resolve members for referenced users
                    foreach (var user in message.GetReferencedUsers())
                        await context.PopulateMemberAsync(user, cancellationToken);

                    // Export the message
                    if (request.MessageFilter.IsMatch(message))
                        await messageExporter.ExportMessageAsync(message, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Provide more context to the exception, to simplify debugging based on error messages
                    throw new DiscordChatExporterException(
                        $"Failed to export message #{message.Id} "
                            + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                            + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                        ex is not DiscordChatExporterException dex || dex.IsFatal,
                        ex
                    );
                }
            }
        }
        finally
        {
            await messageExporter.DisposeAsync();
        }

        return new ExportResult(
            messageExporter.Files,
            messageExporter.MessagesExported,
            context.DownloadedAssetCount
        );
```

(Delete the old `await using var messageExporter = ...` declaration and the original un-wrapped checks/loop that followed it — they are reproduced above inside the `try`.)

- [ ] **Step 4: Build Core**

Run: `dotnet build DiscordChatExporter.Core/DiscordChatExporter.Core.csproj`
Expected: Build succeeded, 0 errors. (Callers that `await` and discard the result still compile.)

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/ExportResult.cs DiscordChatExporter.Core/Exporting/MessageExporter.cs DiscordChatExporter.Core/Exporting/ChannelExporter.cs
git commit -m "Manifest #1: ExportChannelAsync returns ExportResult with per-file stats"
```

---

### Task 3: Manifest data model + JSON options

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestEntry.cs`
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ExportManifest.cs`
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestJson.cs`

- [ ] **Step 1: Create the entry record**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestEntry.cs`:

```csharp
using System;

namespace DiscordChatExporter.Core.Exporting.Manifest;

// One catalogued output file. Self-describing: carries its own guild + channel identity
// so a single manifest can safely span multiple channels and multiple guilds.
public sealed record ManifestEntry(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string? CategoryName,
    string File,
    string Format,
    long MessageCount,
    string? FirstMessageId,
    DateTimeOffset? FirstMessageTimestamp,
    string? LastMessageId,
    DateTimeOffset? LastMessageTimestamp,
    int? AssetCount,
    long FileSizeBytes,
    string Sha256,
    bool Partitioned,
    DateTimeOffset ExportedAt
);
```

- [ ] **Step 2: Create the manifest record**

Create `DiscordChatExporter.Core/Exporting/Manifest/ExportManifest.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public sealed record ExportManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ManifestEntry> Entries
)
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "manifest.json";
}
```

- [ ] **Step 3: Create shared JSON options**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestJson.cs`:

```csharp
using System.Text.Json;

namespace DiscordChatExporter.Core.Exporting.Manifest;

internal static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
```

- [ ] **Step 4: Build Core**

Run: `dotnet build DiscordChatExporter.Core/DiscordChatExporter.Core.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestEntry.cs DiscordChatExporter.Core/Exporting/Manifest/ExportManifest.cs DiscordChatExporter.Core/Exporting/Manifest/ManifestJson.cs
git commit -m "Manifest #1: add manifest data model + JSON options"
```

---

### Task 4: `ManifestReader` (tolerant, versioned)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestReader.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestReaderSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestReaderSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestReaderSpecs
{
    [Fact]
    public async Task Reading_a_missing_file_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        var result = await ManifestReader.TryReadAsync(path);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_garbage_file_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json ]");
        try
        {
            var result = await ManifestReader.TryReadAsync(path);
            result.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reading_a_valid_manifest_round_trips_its_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        var json =
            """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-06-03T10:00:00+00:00",
              "entries": [
                {
                  "guildId": "111",
                  "guildName": "My Server",
                  "channelId": "222",
                  "channelName": "general",
                  "categoryName": "Text",
                  "file": "My Server - general [222].json",
                  "format": "Json",
                  "messageCount": 42,
                  "firstMessageId": "300",
                  "firstMessageTimestamp": "2026-06-01T00:00:00+00:00",
                  "lastMessageId": "900",
                  "lastMessageTimestamp": "2026-06-02T00:00:00+00:00",
                  "assetCount": 5,
                  "fileSizeBytes": 1234,
                  "sha256": "abc123",
                  "partitioned": false,
                  "exportedAt": "2026-06-03T10:00:00+00:00"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, json);
        try
        {
            var result = await ManifestReader.TryReadAsync(path);

            result.Should().NotBeNull();
            result!.SchemaVersion.Should().Be(1);
            result.Entries.Should().HaveCount(1);
            var entry = result.Entries[0];
            entry.ChannelId.Should().Be("222");
            entry.File.Should().Be("My Server - general [222].json");
            entry.MessageCount.Should().Be(42);
            entry.LastMessageId.Should().Be("900");
            entry.Sha256.Should().Be("abc123");
            entry.Partitioned.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestReaderSpecs"`
Expected: FAIL — `ManifestReader` does not exist (compile error).

- [ ] **Step 3: Implement `ManifestReader`**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestReader.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestReader
{
    // Reads and parses a manifest.json. Returns null (never throws) when the file is missing,
    // unreadable, or not valid manifest JSON, so callers can treat "no usable manifest" uniformly.
    public static async ValueTask<ExportManifest?> TryReadAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<ExportManifest>(
                stream,
                ManifestJson.Options,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestReaderSpecs"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestReader.cs DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestReaderSpecs.cs
git commit -m "Manifest #1: add tolerant ManifestReader + tests"
```

---

### Task 5: `ManifestWriter` (atomic merge-by-file)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestWriter.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestWriterSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestWriterSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestWriterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"dce-manifest-{Guid.NewGuid():N}"
    );

    public ManifestWriterSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static ManifestEntry Entry(string file, long messageCount) =>
        new(
            GuildId: "1",
            GuildName: "g",
            ChannelId: "2",
            ChannelName: "c",
            CategoryName: null,
            File: file,
            Format: "Json",
            MessageCount: messageCount,
            FirstMessageId: "10",
            FirstMessageTimestamp: DateTimeOffset.UnixEpoch,
            LastMessageId: "20",
            LastMessageTimestamp: DateTimeOffset.UnixEpoch,
            AssetCount: 0,
            FileSizeBytes: 1,
            Sha256: "x",
            Partitioned: false,
            ExportedAt: DateTimeOffset.UnixEpoch
        );

    [Fact]
    public async Task Writing_creates_a_manifest_with_the_given_entries_and_schema_version()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().Be(ExportManifest.CurrentSchemaVersion);
        manifest.Entries.Should().ContainSingle(e => e.File == "a.json");
    }

    [Fact]
    public async Task Writing_again_replaces_entries_for_the_same_file_and_keeps_the_others()
    {
        await ManifestWriter.WriteAsync(
            _dir,
            [Entry("a.json", 1), Entry("b.json", 1)],
            DateTimeOffset.UnixEpoch
        );
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 99)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest!.Entries.Should().HaveCount(2);
        manifest.Entries.Single(e => e.File == "a.json").MessageCount.Should().Be(99);
        manifest.Entries.Single(e => e.File == "b.json").MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Writing_over_an_existing_manifest_leaves_a_backup()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 2)], DateTimeOffset.UnixEpoch);

        File.Exists(Path.Combine(_dir, ExportManifest.FileName + ".bak")).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestWriterSpecs"`
Expected: FAIL — `ManifestWriter` does not exist.

- [ ] **Step 3: Implement `ManifestWriter`**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestWriter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestWriter
{
    // Writes/updates <dirPath>/manifest.json by merging the given entries into any existing
    // manifest, keyed by file name (a new entry for the same file replaces the old one).
    // Atomic: writes a temp file then swaps it in, keeping a .bak of the previous manifest.
    public static async ValueTask WriteAsync(
        string dirPath,
        IReadOnlyList<ManifestEntry> newEntries,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        var manifestPath = Path.Combine(dirPath, ExportManifest.FileName);

        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var existing = await ManifestReader.TryReadAsync(manifestPath, cancellationToken);
        if (existing is not null)
        {
            foreach (var entry in existing.Entries)
                byFile[entry.File] = entry;
        }

        foreach (var entry in newEntries)
            byFile[entry.File] = entry;

        var merged = new ExportManifest(
            ExportManifest.CurrentSchemaVersion,
            now,
            byFile.Values.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToArray()
        );

        Directory.CreateDirectory(dirPath);

        var tempPath = manifestPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, merged, ManifestJson.Options, cancellationToken);
        }

        if (File.Exists(manifestPath))
            File.Replace(tempPath, manifestPath, manifestPath + ".bak");
        else
            File.Move(tempPath, manifestPath);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestWriterSpecs"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestWriter.cs DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestWriterSpecs.cs
git commit -m "Manifest #1: add atomic merge-by-file ManifestWriter + tests"
```

---

### Task 6: `ManifestBuilder` (ExportResult → entries)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestChannelInfo.cs`
- Create: `DiscordChatExporter.Core/Exporting/Manifest/ManifestBuilder.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestBuilderSpecs.cs`

- [ ] **Step 1: Write the failing test**

Create `DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestBuilderSpecs.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestBuilderSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"dce-manifest-{Guid.NewGuid():N}"
    );

    public ManifestBuilderSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ManifestChannelInfo Info() =>
        new("1", "Guild", "2", "general", "Text", "Json");

    [Fact]
    public void A_single_file_export_produces_one_self_describing_entry()
    {
        var path = WriteFile("general.json", "hello");
        var result = new ExportResult(
            [
                new ExportedFile(
                    path,
                    3,
                    new Snowflake(100),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(300),
                    DateTimeOffset.UnixEpoch.AddHours(1)
                ),
            ],
            3,
            7
        );

        var entries = ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.GuildId.Should().Be("1");
        entry.ChannelId.Should().Be("2");
        entry.CategoryName.Should().Be("Text");
        entry.File.Should().Be("general.json");
        entry.MessageCount.Should().Be(3);
        entry.FirstMessageId.Should().Be("100");
        entry.LastMessageId.Should().Be("300");
        entry.AssetCount.Should().Be(7);
        entry.FileSizeBytes.Should().Be(5); // "hello"
        entry.Sha256.Should().NotBeNullOrEmpty();
        entry.Partitioned.Should().BeFalse();
    }

    [Fact]
    public void A_partitioned_export_produces_one_entry_per_file_all_flagged_partitioned()
    {
        var p1 = WriteFile("a.json", "x");
        var p2 = WriteFile("b.json", "yy");
        var result = new ExportResult(
            [
                new ExportedFile(p1, 1, new Snowflake(1), DateTimeOffset.UnixEpoch, new Snowflake(2), DateTimeOffset.UnixEpoch),
                new ExportedFile(p2, 1, new Snowflake(3), DateTimeOffset.UnixEpoch, new Snowflake(4), DateTimeOffset.UnixEpoch),
            ],
            2,
            0
        );

        var entries = ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch);

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Partitioned);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestBuilderSpecs"`
Expected: FAIL — `ManifestChannelInfo`/`ManifestBuilder` do not exist.

- [ ] **Step 3: Implement the channel-info record and the builder**

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestChannelInfo.cs`:

```csharp
namespace DiscordChatExporter.Core.Exporting.Manifest;

// The channel/guild identity for a manifest entry, supplied by the caller (which holds the
// ExportRequest). Keeps ManifestBuilder decoupled from the Discord data types so it is trivially
// unit-testable.
public sealed record ManifestChannelInfo(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string? CategoryName,
    string Format
);
```

Create `DiscordChatExporter.Core/Exporting/Manifest/ManifestBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestBuilder
{
    // Builds one self-describing manifest entry per output file in the result.
    // `assetCount` is recorded as best-effort and only when assets were actually resolved.
    public static IReadOnlyList<ManifestEntry> Build(
        ManifestChannelInfo info,
        ExportResult result,
        DateTimeOffset now
    )
    {
        var partitioned = result.Files.Count > 1;
        var entries = new List<ManifestEntry>(result.Files.Count);

        foreach (var file in result.Files)
        {
            var fileName = Path.GetFileName(file.FilePath);
            var sizeBytes = new FileInfo(file.FilePath).Length;
            var sha256 = ComputeSha256(file.FilePath);

            entries.Add(
                new ManifestEntry(
                    GuildId: info.GuildId,
                    GuildName: info.GuildName,
                    ChannelId: info.ChannelId,
                    ChannelName: info.ChannelName,
                    CategoryName: info.CategoryName,
                    File: fileName,
                    Format: info.Format,
                    MessageCount: file.MessageCount,
                    FirstMessageId: file.FirstMessageId?.ToString(),
                    FirstMessageTimestamp: file.FirstMessageTimestamp,
                    LastMessageId: file.LastMessageId?.ToString(),
                    LastMessageTimestamp: file.LastMessageTimestamp,
                    AssetCount: result.AssetCount > 0 ? result.AssetCount : null,
                    FileSizeBytes: sizeBytes,
                    Sha256: sha256,
                    Partitioned: partitioned,
                    ExportedAt: now
                )
            );
        }

        return entries;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~ManifestBuilderSpecs"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Manifest/ManifestChannelInfo.cs DiscordChatExporter.Core/Exporting/Manifest/ManifestBuilder.cs DiscordChatExporter.Cli.Tests/Specs/Manifest/ManifestBuilderSpecs.cs
git commit -m "Manifest #1: add ManifestBuilder (ExportResult to entries) + tests"
```

---

### Task 7: Wire manifest writing into the GUI export

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

- [ ] **Step 1: Add a localization string for write failure**

In `LocalizationManager.cs`, in the Dashboard region (near `EtaRemainingFormat`), add:

```csharp
    public string ExportCatalogWriteFailedMessage => Get();
```

In `LocalizationManager.English.cs`, in the Dashboard section of the dictionary (near `[nameof(EtaRemainingFormat)]`), add:

```csharp
            [nameof(ExportCatalogWriteFailedMessage)] =
                "Export finished, but the catalog (manifest.json) could not be updated.",
```

- [ ] **Step 2: Add usings**

In `DashboardViewModel.cs`, add to the using block:

```csharp
using System.Collections.Concurrent;
using DiscordChatExporter.Core.Exporting.Manifest;
```

- [ ] **Step 3: Collect manifest data in the export loop**

In `ExportAsync`, immediately before `var channelProgressPairs =` add:

```csharp
            var manifestData =
                new ConcurrentBag<(string Dir, ManifestChannelInfo Info, ExportResult Result)>();
```

Inside the `Parallel.ForEachAsync` body, change the export call from:

```csharp
                        await exporter.ExportChannelAsync(request, progress, cancellationToken);

                        Interlocked.Increment(ref successfulExportCount);
```

to:

```csharp
                        var result = await exporter.ExportChannelAsync(
                            request,
                            progress,
                            cancellationToken
                        );

                        manifestData.Add(
                            (
                                request.OutputDirPath,
                                new ManifestChannelInfo(
                                    request.Guild.Id.ToString(),
                                    request.Guild.Name,
                                    request.Channel.Id.ToString(),
                                    request.Channel.Name,
                                    request.Channel.Parent?.Name,
                                    request.Format.ToString()
                                ),
                                result
                            )
                        );

                        Interlocked.Increment(ref successfulExportCount);
```

- [ ] **Step 4: Write the manifest(s) after the loop**

In `ExportAsync`, after the `Parallel.ForEachAsync(...)` call returns and before the `// Notify of the overall completion` block, add:

```csharp
            // Write/update the export catalog (best-effort: never fail the export over it)
            if (!manifestData.IsEmpty)
            {
                try
                {
                    var now = DateTimeOffset.Now;
                    foreach (var group in manifestData.GroupBy(d => d.Dir))
                    {
                        var entries = group
                            .SelectMany(d => ManifestBuilder.Build(d.Info, d.Result, now))
                            .ToArray();

                        await ManifestWriter.WriteAsync(group.Key, entries, now);
                    }
                }
                catch
                {
                    _snackbarManager.Notify(
                        LocalizationManager.ExportCatalogWriteFailedMessage.TrimEnd('.')
                    );
                }
            }
```

- [ ] **Step 5: Build the GUI**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Manifest #1: write manifest.json after GUI export (best-effort)"
```

---

### Task 8: Full verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build DiscordChatExporter.slnx`
Expected: Build succeeded, 0 errors across Core, Cli, Gui.

- [ ] **Step 2: Run all manifest tests**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Manifest"`
Expected: PASS (8 tests: 3 reader + 3 writer + 2 builder).

- [ ] **Step 3: Run the existing continuation + eta tests to confirm no regressions**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation|FullyQualifiedName~Eta"`
Expected: PASS (the prior suite, unchanged).

- [ ] **Step 4: Commit any final touch-ups (if needed)**

```bash
git add -A
git commit -m "Manifest #1: verification pass"
```

---

## Self-Review

**Spec coverage:** sidecar `manifest.json` ✔ (Task 5/7); per-file self-describing entries ✔ (Task 3/6); guild+channel identity, format, msg count, first/last id+ts, asset count, size, sha256 ✔ (Task 3/6); versioned schema ✔ (`CurrentSchemaVersion`); write once after parallel loop ✔ (Task 7); atomic + .bak ✔ (Task 5); best-effort ✔ (Task 7); tolerant reader for #5/#3 ✔ (Task 4).

**Manual smoke (needs token, user-run):** export a server to a folder → confirm `manifest.json` lists one entry per channel file with correct counts; re-export one channel → its entry updates and a `.bak` appears; the entry's `lastMessageId` for a JSON file equals `JsonExportInspector`'s `LastMessageId` for that same file (ties the foundation to trusted continuation code).

**Type consistency:** `ExportResult`/`ExportedFile` (Task 2) used verbatim by `ManifestBuilder` (Task 6) and the GUI (Task 7); `ManifestChannelInfo` fields match the GUI's constructor call; `ExportManifest.FileName`/`CurrentSchemaVersion` referenced consistently.
