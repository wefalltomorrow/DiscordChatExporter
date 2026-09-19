# Incremental JSON Export ("Continue export") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-optimized:subagent-driven-development (recommended) or superpowers-optimized:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a GUI "Continue export…" flow that appends only-newer messages to an existing DCE JSON export, merging them into the same file.

**Architecture:** Two new streaming Core classes — `JsonExportInspector` (reads an existing export's cutoff: guild/channel IDs, last message ID, count, chronological-order check) and `JsonExportMerger` (token-pump merge of existing + freshly-exported new messages into one file via temp + atomic `File.Replace`). The GUI `DashboardViewModel` gains a `ContinueExportCommand` that picks a file, inspects it, resolves the live `Guild`/`Channel`, runs the existing `ChannelExporter` with `After = lastMessageId` into a temp file, then merges. New-message serialization fully reuses the unmodified `JsonMessageWriter`.

**Tech Stack:** C# / .NET 10, Avalonia + CommunityToolkit.Mvvm (GUI), System.Text.Json (`Utf8JsonReader`/`Utf8JsonWriter`), xunit + FluentAssertions (tests). Build is self-contained `win-x64` with `PublishTrimmed=true` (new Core code is reflection-free → trim-safe).

**Assumptions:**
- Original export is **chronological order** (oldest→newest). Assumes default export order — will NOT continue a `--reverse` export (detected via `IsChronological` and refused).
- Original export is a **single, non-partitioned** file. Assumes default partitioning off — will NOT correctly continue partitioned (`[part N]`) exports (detected by name/sibling and refused).
- Cutoff is read from the file's last `messages[]` element ID. Assumes the file is a DCE JSON export with ≥1 message — empty/non-DCE files are refused.
- New messages adopt **persisted** markdown/locale/UTC settings (the file doesn't record them). Assumes a cosmetic-only mismatch is acceptable.
- Continue does **not** download media. Assumes new messages may reference remote Discord CDN URLs.
- Files fit in memory as raw bytes (read via `File.ReadAllBytesAsync`, ~1× file size, no DOM). Assumes realistic personal-export sizes (≤ a few hundred MB).

---

## File Structure

| Path | Responsibility |
|---|---|
| `DiscordChatExporter.Core/Exporting/Continuation/JsonExportInfo.cs` | Immutable result of inspecting an export (record). |
| `DiscordChatExporter.Core/Exporting/Continuation/InvalidJsonExportException.cs` | Non-fatal exception for unreadable/non-DCE exports. |
| `DiscordChatExporter.Core/Exporting/Continuation/JsonExportInspector.cs` | Streams an existing export → `JsonExportInfo`. |
| `DiscordChatExporter.Core/Exporting/Continuation/JsonExportMerger.cs` | Token-pump merge existing + new messages → one file, atomic replace. |
| `DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportInspectorSpecs.cs` | Unit tests (no network). |
| `DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportMergerSpecs.cs` | Unit tests (no network). |
| `DiscordChatExporter.Gui/Framework/DialogManager.cs` | Add `PromptSingleFilePathAsync` (open-file picker). |
| `DiscordChatExporter.Gui/Localization/LocalizationManager.cs` | Add string properties. |
| `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs` | Add English dictionary entries. |
| `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` | `ContinueExportCommand` + orchestration. |
| `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml` | "Continue export…" button. |

**Test execution (no Discord token needed):**
`dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation"`

---

### Task 1: `JsonExportInfo` + `InvalidJsonExportException` + `JsonExportInspector`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/JsonExportInfo.cs`
- Create: `DiscordChatExporter.Core/Exporting/Continuation/InvalidJsonExportException.cs`
- Create: `DiscordChatExporter.Core/Exporting/Continuation/JsonExportInspector.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportInspectorSpecs.cs`

**Security flag:** `security` *(parses an externally-provided file; validation handled by System.Text.Json + explicit structure checks + try/catch wrapping into a non-fatal exception)*

**Does NOT cover:** Detecting partitioned siblings (done in Task 5). Detecting reverse order is covered here only as the `IsChronological` flag — the *refusal* is enforced by the caller (Task 5). A file with exactly 1 message yields `IsChronological = true` (first == last).

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportInspectorSpecs
{
    private static async Task<string> WriteTempAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private const string TwoMessages = """
    {
      "guild": { "id": "111", "name": "G" },
      "channel": { "id": "222", "name": "C" },
      "dateRange": { "after": null, "before": null },
      "exportedAt": "2021-01-01T00:00:00+00:00",
      "messages": [
        { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
        { "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }
      ],
      "messageCount": 2
    }
    """;

    [Fact]
    public async Task It_reads_guild_channel_cutoff_and_count_from_a_valid_export()
    {
        var path = await WriteTempAsync(TwoMessages);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);

            info.GuildId.Value.Should().Be(111UL);
            info.ChannelId.Value.Should().Be(222UL);
            info.LastMessageId.Value.Should().Be(2000UL);
            info.MessageCount.Should().Be(2);
            info.IsChronological.Should().BeTrue();
            info.Before.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_flags_a_reverse_ordered_export_as_not_chronological()
    {
        var reversed = TwoMessages
            .Replace("\"2021-07-19T13:34:18+00:00\"", "\"2021-07-24T13:49:13+00:00\"X")
            .Replace("\"2021-07-24T13:49:13+00:00\"", "\"2021-07-19T13:34:18+00:00\"")
            .Replace("\"2021-07-19T13:34:18+00:00\"X", "\"2021-07-24T13:49:13+00:00\"");
        var path = await WriteTempAsync(reversed);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);
            info.IsChronological.Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_rejects_an_export_with_no_messages()
    {
        var empty = TwoMessages
            .Replace("""
            "messages": [
                { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
                { "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }
              ],
            """, "\"messages\": [],")
            .Replace("\"messageCount\": 2", "\"messageCount\": 0");
        var path = await WriteTempAsync(empty);
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_rejects_a_non_dce_json_file()
    {
        var path = await WriteTempAsync("""{ "hello": "world" }""");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_rejects_malformed_json()
    {
        var path = await WriteTempAsync("{ not json");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~JsonExportInspectorSpecs"`
Expected: FAIL — does not compile (`JsonExportInspector`/`JsonExportInfo`/`InvalidJsonExportException` do not exist yet).

- [ ] **Step 3: Implement**

`JsonExportInfo.cs`:
```csharp
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public sealed record JsonExportInfo(
    Snowflake GuildId,
    Snowflake ChannelId,
    Snowflake? Before,
    Snowflake LastMessageId,
    long MessageCount,
    bool IsChronological
);
```

`InvalidJsonExportException.cs`:
```csharp
using System;
using DiscordChatExporter.Core.Exceptions;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public class InvalidJsonExportException(string message, Exception? innerException = null)
    : DiscordChatExporterException(message, false, innerException);
```

`JsonExportInspector.cs`:
```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportInspector
{
    public static async ValueTask<JsonExportInfo> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidJsonExportException($"Could not read '{filePath}'.", ex);
        }

        try
        {
            return Inspect(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidJsonExportException(
                "The selected file is not a valid JSON export.",
                ex
            );
        }
    }

    private static JsonExportInfo Inspect(byte[] bytes)
    {
        Snowflake? guildId = null;
        Snowflake? channelId = null;
        Snowflake? before = null;
        Snowflake? firstId = null;
        Snowflake? lastId = null;
        DateTimeOffset? firstTs = null;
        DateTimeOffset? lastTs = null;
        long count = 0;

        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("The selected file is not a JSON export.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read(); // advance to value

            switch (name)
            {
                case "guild":
                    guildId = ReadIdOf(ref reader);
                    break;
                case "channel":
                    channelId = ReadIdOf(ref reader);
                    break;
                case "dateRange":
                    before = ReadBeforeOf(ref reader);
                    break;
                case "messages":
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new InvalidJsonExportException("Malformed 'messages' array.");
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var (id, ts) = ReadMessageHeader(ref reader);
                        firstId ??= id;
                        firstTs ??= ts;
                        lastId = id;
                        lastTs = ts;
                        count++;
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (guildId is null || channelId is null)
            throw new InvalidJsonExportException(
                "The selected file is not a DiscordChatExporter JSON export."
            );

        if (count == 0 || lastId is null)
            throw new InvalidJsonExportException(
                "The selected export contains no messages to continue from."
            );

        var isChronological = firstTs is null || lastTs is null || firstTs <= lastTs;

        return new JsonExportInfo(
            guildId.Value,
            channelId.Value,
            before,
            lastId.Value,
            count,
            isChronological
        );
    }

    // Reader is positioned on the StartObject of an object that has a string "id" property.
    // Returns that id and leaves the reader on the object's EndObject.
    private static Snowflake ReadIdOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("Expected an object.");

        Snowflake? id = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else
                reader.Skip();
        }
        return id ?? throw new InvalidJsonExportException("Missing 'id'.");
    }

    private static Snowflake? ReadBeforeOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        Snowflake? before = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "before" && reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (
                    !string.IsNullOrWhiteSpace(raw)
                    && DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dto
                    )
                )
                {
                    before = Snowflake.FromDate(dto);
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return before;
    }

    // Reader positioned on the StartObject of a message. Returns id + timestamp,
    // leaves the reader on the message's EndObject.
    private static (Snowflake Id, DateTimeOffset? Timestamp) ReadMessageHeader(
        ref Utf8JsonReader reader
    )
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("Malformed message entry.");

        Snowflake? id = null;
        DateTimeOffset? ts = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else if (prop == "timestamp" && reader.TokenType == JsonTokenType.String)
                ts = reader.TryGetDateTimeOffset(out var dto) ? dto : null;
            else
                reader.Skip();
        }
        return (id ?? throw new InvalidJsonExportException("Message missing 'id'."), ts);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~JsonExportInspectorSpecs"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/JsonExportInfo.cs DiscordChatExporter.Core/Exporting/Continuation/InvalidJsonExportException.cs DiscordChatExporter.Core/Exporting/Continuation/JsonExportInspector.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportInspectorSpecs.cs
git commit -m "Add JsonExportInspector for reading continuation cutoff from a JSON export"
```

---

### Task 2: `JsonExportMerger`

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Continuation/JsonExportMerger.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportMergerSpecs.cs`

**Security flag:** `security` *(overwrites a user file; mitigated by writing to a temp file then atomic `File.Replace` with a `.bak` backup, so the original is never partially written)*

**Does NOT cover:** Merging when either file is not a well-formed DCE JSON export (the caller only invokes this after `JsonExportInspector` validates the existing file, and the new-messages file is produced by this tool's own writer). Does not de-duplicate — relies on the caller's exclusive `After` cursor to guarantee no overlap.

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportMergerSpecs
{
    private static string Existing(string messages, int count) => $$"""
    {
      "guild": { "id": "111", "name": "G" },
      "channel": { "id": "222", "name": "C" },
      "dateRange": { "after": null, "before": null },
      "exportedAt": "2021-01-01T00:00:00+00:00",
      "messages": [{{messages}}],
      "messageCount": {{count}}
    }
    """;

    private static string NewExport(string messages, int count) => Existing(messages, count);

    private const string MsgA =
        """{ "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" }""";
    private const string MsgB =
        """{ "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }""";
    private const string MsgC =
        """{ "id": "3000", "type": "Default", "timestamp": "2021-07-25T10:00:00+00:00", "content": "c" }""";

    private static async Task<string> WriteAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-merge-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task It_appends_new_messages_and_fixes_the_count()
    {
        var existing = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        var fresh = await WriteAsync(NewExport(MsgC, 1));
        try
        {
            var total = await JsonExportMerger.MergeAsync(
                existing,
                fresh,
                new DateTimeOffset(2026, 06, 03, 0, 0, 0, TimeSpan.Zero)
            );

            total.Should().Be(3);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var root = doc.RootElement;
            var ids = root.GetProperty("messages")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString())
                .ToArray();
            ids.Should().Equal("1000", "2000", "3000");
            root.GetProperty("messageCount").GetInt64().Should().Be(3);
            root.GetProperty("guild").GetProperty("id").GetString().Should().Be("111");
            root.GetProperty("exportedAt").GetString().Should().Contain("2026-06-03");

            File.Exists(existing + ".bak").Should().BeTrue();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_appends_into_an_export_that_had_no_messages()
    {
        var existing = await WriteAsync(Existing("", 0));
        var fresh = await WriteAsync(NewExport($"{MsgA},{MsgB}", 2));
        try
        {
            var total = await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            total.Should().Be(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(2);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_produces_valid_json_whose_count_matches_actual_elements()
    {
        var existing = await WriteAsync(Existing(MsgA, 1));
        var fresh = await WriteAsync(NewExport($"{MsgB},{MsgC}", 2));
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var actual = doc.RootElement.GetProperty("messages").GetArrayLength();
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(actual);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak")) File.Delete(existing + ".bak");
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~JsonExportMergerSpecs"`
Expected: FAIL — does not compile (`JsonExportMerger` does not exist).

- [ ] **Step 3: Implement**

`JsonExportMerger.cs`:
```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportMerger
{
    // Merges the 'messages' from newMessagesFilePath into existingFilePath, preserving the
    // existing preamble (guild/channel/dateRange), refreshing exportedAt, and recomputing
    // messageCount. Writes to a temp file then atomically replaces the original (with .bak).
    // Returns the merged total message count.
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    )
    {
        var existingBytes = await File.ReadAllBytesAsync(existingFilePath, cancellationToken);
        var newBytes = await File.ReadAllBytesAsync(newMessagesFilePath, cancellationToken);

        var tempPath = existingFilePath + ".merging.tmp";
        long total;

        await using (var outStream = File.Create(tempPath))
        {
            await using var writer = new Utf8JsonWriter(
                outStream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = true,
                    SkipValidation = true,
                }
            );

            total = Merge(existingBytes, newBytes, exportedAt, writer);
            await writer.FlushAsync(cancellationToken);
        }

        File.Replace(tempPath, existingFilePath, existingFilePath + ".bak");
        return total;
    }

    private static long Merge(
        byte[] existingBytes,
        byte[] newBytes,
        DateTimeOffset exportedAt,
        Utf8JsonWriter writer
    )
    {
        long total = 0;

        var reader = new Utf8JsonReader(existingBytes);
        reader.Read(); // StartObject (root)
        writer.WriteStartObject();

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read(); // advance to value

            switch (name)
            {
                case "exportedAt":
                    writer.WriteString(
                        "exportedAt",
                        exportedAt.ToString(
                            "yyyy-MM-ddTHH:mm:ss.fffzzz",
                            CultureInfo.InvariantCulture
                        )
                    );
                    break; // value scalar already consumed positionally

                case "messageCount":
                    break; // drop; recomputed below

                case "messages":
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        CopyValue(ref reader, writer);
                        total++;
                    }
                    total += AppendNewMessages(newBytes, writer);
                    writer.WriteEndArray();
                    break;

                default:
                    writer.WritePropertyName(name!);
                    CopyValue(ref reader, writer);
                    break;
            }
        }

        writer.WriteNumber("messageCount", total);
        writer.WriteEndObject();
        return total;
    }

    private static long AppendNewMessages(byte[] newBytes, Utf8JsonWriter writer)
    {
        long count = 0;
        var reader = new Utf8JsonReader(newBytes);
        reader.Read(); // StartObject (root)

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "messages")
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    CopyValue(ref reader, writer);
                    count++;
                }
                break;
            }
            reader.Skip();
        }
        return count;
    }

    // Copies the complete JSON value the reader is currently positioned on (scalar or
    // container subtree), leaving the reader on that value's final token.
    private static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        if (
            reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray
        )
        {
            var depth = 0;
            do
            {
                CopyToken(ref reader, writer);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    depth--;

                if (depth == 0)
                    break;
                reader.Read();
            } while (true);
        }
        else
        {
            CopyToken(ref reader, writer);
        }
    }

    private static void CopyToken(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                break;
            case JsonTokenType.EndObject:
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                break;
            case JsonTokenType.EndArray:
                writer.WriteEndArray();
                break;
            case JsonTokenType.PropertyName:
                writer.WritePropertyName(reader.GetString()!);
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~JsonExportMergerSpecs"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Core/Exporting/Continuation/JsonExportMerger.cs DiscordChatExporter.Cli.Tests/Specs/Continuation/JsonExportMergerSpecs.cs
git commit -m "Add JsonExportMerger to splice new messages into an existing JSON export"
```

---

### Task 3: `DialogManager.PromptSingleFilePathAsync` (open-file picker)

**Files:**
- Modify: `DiscordChatExporter.Gui/Framework/DialogManager.cs`

**Security flag:** `none`

- [ ] **Step 1: Implement** (no unit test — Avalonia storage provider needs a UI host; verified by build + Task 7 smoke)

Add this method to `DialogManager` (after `PromptDirectoryPathAsync`), mirroring the existing methods:

```csharp
public async Task<string?> PromptSingleFilePathAsync(
    IReadOnlyList<FilePickerFileType>? fileTypes = null,
    string defaultDirPath = ""
)
{
    var topLevel =
        Application.Current?.ApplicationLifetime?.TryGetTopLevel()
        ?? throw new ApplicationException("Could not find the top-level visual element.");

    var files = await topLevel.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
            SuggestedStartLocation = string.IsNullOrWhiteSpace(defaultDirPath)
                ? null
                : await topLevel.StorageProvider.TryGetFolderFromPathAsync(defaultDirPath),
        }
    );

    var file = files.FirstOrDefault();
    if (file is null)
        return null;

    return file.TryGetLocalPath() ?? file.Path.ToString();
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: PASS (0 errors).

- [ ] **Step 3: Commit**

```bash
git add DiscordChatExporter.Gui/Framework/DialogManager.cs
git commit -m "Add open-file picker to DialogManager"
```

---

### Task 4: Localization strings

**Files:**
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.cs`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs`

**Security flag:** `none`

- [ ] **Step 1: Add string properties** in `LocalizationManager.cs`, in the `// ---- Dashboard ----` group (next to `PullGuildsTooltip`):

```csharp
    public string ContinueExportTooltip => Get();
    public string ContinueExportUpToDateMessage => Get();
    public string ContinueExportSuccessMessage => Get();
    public string ContinueExportReverseUnsupportedMessage => Get();
    public string ContinueExportPartitionedUnsupportedMessage => Get();
```

- [ ] **Step 2: Add English entries.** First inspect the dictionary format:

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug` *(expect FAIL — missing keys at runtime only, but build passes; this step is to open the file)*

In `LocalizationManager.English.cs`, locate the `EnglishLocalization` dictionary's Dashboard section (it has an entry for `["PullGuildsTooltip"] = "..."`) and add:

```csharp
        ["ContinueExportTooltip"] = "Continue an existing JSON export (add new messages)",
        ["ContinueExportUpToDateMessage"] = "That export is already up to date.",
        ["ContinueExportSuccessMessage"] = "Added {0} new message(s).",
        ["ContinueExportReverseUnsupportedMessage"] =
            "Continuing reverse-ordered exports is not supported.",
        ["ContinueExportPartitionedUnsupportedMessage"] =
            "Continuing partitioned exports is not supported.",
```

- [ ] **Step 3: Verify build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: PASS (0 errors).

- [ ] **Step 4: Commit**

```bash
git add DiscordChatExporter.Gui/Localization/LocalizationManager.cs DiscordChatExporter.Gui/Localization/LocalizationManager.English.cs
git commit -m "Add localization strings for Continue export"
```

---

### Task 5: `DashboardViewModel.ContinueExportCommand` orchestration

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`

**Security flag:** `security` *(file IO on a user-chosen path + writes over the original via the merger; refuses unsupported inputs; resolves channel/guild via the already-authenticated DiscordClient)*

**Does NOT cover:** Partitioned-export detection here is name/sibling based — it refuses when the chosen file name contains `" [part "` or a `" [part 2]"` sibling exists. It does NOT detect partitioned sets produced via custom `%`-template output paths or renamed files. Media download on continue is intentionally not performed.

- [ ] **Step 1: Add usings** (top of file, with the other `using` lines):

```csharp
using System.Linq;
using Avalonia.Platform.Storage;
using DiscordChatExporter.Core.Exporting.Continuation;
```
*(System.IO, System.Threading, System.Threading.Tasks, DiscordChatExporter.Core.Discord, ...Exporting, ...Exceptions, ...Filtering, ...Partitioning, Gress, PowerKit.Extensions are already imported.)*

- [ ] **Step 2: Add the command + helper** inside `DashboardViewModel`, after the `ExportAsync` method:

```csharp
    private bool CanContinueExport() => !IsBusy && _discord is not null;

    [RelayCommand(CanExecute = nameof(CanContinueExport))]
    private async Task ContinueExportAsync()
    {
        if (_discord is null)
            return;

        // Pick the existing JSON export
        var filePath = await _dialogManager.PromptSingleFilePathAsync(
            [new FilePickerFileType("JSON export") { Patterns = ["*.json"] }]
        );
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        // Refuse partitioned exports (name- or sibling-based detection)
        if (IsPartitionedExportPath(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportPartitionedUnsupportedMessage.TrimEnd('.')
            );
            return;
        }

        IsBusy = true;
        var progress = _progressMuxer.CreateInput();
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"{Program.Name}-continue-{Guid.NewGuid():N}.json"
        );

        try
        {
            var info = await JsonExportInspector.InspectAsync(filePath);

            if (!info.IsChronological)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportReverseUnsupportedMessage.TrimEnd('.')
                );
                return;
            }

            // Resolve live channel + guild
            var channel = await _discord.GetChannelAsync(info.ChannelId);
            var guild = channel.IsDirect
                ? Guild.DirectMessages
                : await _discord.GetGuildAsync(channel.GuildId);

            // Export only messages after the recorded cutoff into a temp file
            var request = new ExportRequest(
                guild,
                channel,
                tempPath,
                null,
                ExportFormat.Json,
                info.LastMessageId, // exact, exclusive cursor
                info.Before,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                _settingsService.LastShouldFormatMarkdown,
                false,
                false,
                _settingsService.Locale,
                _settingsService.IsUtcNormalizationEnabled
            );

            var exporter = new ChannelExporter(_discord);

            try
            {
                await exporter.ExportChannelAsync(request, progress);
            }
            catch (ChannelEmptyException)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
                return;
            }

            // Merge the new messages into the original file
            var addedBefore = info.MessageCount;
            var total = await JsonExportMerger.MergeAsync(filePath, tempPath, DateTimeOffset.Now);
            var added = total - addedBefore;

            if (added <= 0)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ContinueExportSuccessMessage, added)
                );
            }
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );
            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary export.
            }
        }
    }

    private static bool IsPartitionedExportPath(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Contains(" [part ", StringComparison.OrdinalIgnoreCase))
            return true;

        var dir = Path.GetDirectoryName(filePath);
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(dir))
            return false;

        var sibling = Path.Combine(dir, $"{fileName} [part 2]{ext}");
        return File.Exists(sibling);
    }
```

*(Note: `[RelayCommand]` source-generates `ContinueExportCommand`; add `[NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]` to the `IsBusy` property's attribute list so the button re-evaluates when busy state changes, matching how `ExportCommand` is wired.)*

- [ ] **Step 3: Wire CanExecute re-evaluation.** On the `IsBusy` property, add to its attribute block (alongside the existing `[NotifyCanExecuteChangedFor(...)]` lines):

```csharp
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
```

Also, after `_discord` is assigned in `PullGuildsAsync` the command should enable; it already re-runs through `IsBusy` toggling in the `finally`, which fires the notify. No extra change needed.

- [ ] **Step 4: Verify build**

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: PASS (0 errors). The generated `ContinueExportCommand` property now exists for the view binding in Task 6.

- [ ] **Step 5: Commit**

```bash
git add DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs
git commit -m "Add ContinueExport orchestration to DashboardViewModel"
```

---

### Task 6: "Continue export…" button in `DashboardView.axaml`

**Files:**
- Modify: `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml`

**Security flag:** `none`

- [ ] **Step 1: Add the button** immediately before the existing `<!--  Export button  -->` comment/`<Button>` (so it sits to the left of the Export FAB). It reuses the same FAB styling with a left offset:

```xml
            <!--  Continue export button  -->
            <Button
                Width="56"
                Height="56"
                Margin="32,24,104,24"
                Padding="0"
                HorizontalAlignment="Right"
                VerticalAlignment="Bottom"
                Background="{DynamicResource MaterialDarkBackgroundBrush}"
                Command="{Binding ContinueExportCommand}"
                Foreground="{DynamicResource MaterialDarkForegroundBrush}"
                IsVisible="{Binding $self.IsEffectivelyEnabled}"
                Theme="{DynamicResource MaterialIconButton}"
                ToolTip.Tip="{Binding LocalizationManager.ContinueExportTooltip}">
                <materialIcons:MaterialIcon
                    Width="32"
                    Height="32"
                    Kind="FileDocumentPlusOutline" />
            </Button>
```

- [ ] **Step 2: Verify build** (Avalonia XAML compiles during build)

Run: `dotnet build DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Debug`
Expected: PASS (0 errors). If `Kind="FileDocumentPlusOutline"` is not a valid Material.Icons value, the build fails on the XAML — substitute a valid icon (e.g. `Plus` or `Update`).

- [ ] **Step 3: Commit**

```bash
git add DiscordChatExporter.Gui/Views/Components/DashboardView.axaml
git commit -m "Add Continue export button to the dashboard"
```

---

### Task 7: Build & publish the win-x64 GUI executable

**Files:** none (build artifact only)

**Security flag:** `none`

- [ ] **Step 1: Run the full Core continuation test suite**

Run: `dotnet test DiscordChatExporter.Cli.Tests/DiscordChatExporter.Cli.Tests.csproj --filter "FullyQualifiedName~Continuation"`
Expected: PASS (8 tests total).

- [ ] **Step 2: Publish the self-contained win-x64 GUI**

Run: `dotnet publish DiscordChatExporter.Gui/DiscordChatExporter.Gui.csproj -c Release -r win-x64`
Expected: PASS. Produces `DiscordChatExporter.Gui/bin/Release/net10.0/win-x64/publish/DiscordChatExporter.exe`.

- [ ] **Step 3: Manual smoke test (operator)**

Launch the published `DiscordChatExporter.exe`, paste a token, pull guilds, click the new "Continue export…" button, pick an existing JSON export, and confirm: new messages are appended, `messageCount` updated, a `.bak` is created, and an "already up to date" notice appears when there is nothing new. *(Cannot be automated — Avalonia desktop app.)*

- [ ] **Step 4: (Optional) Replace the user-facing copy**

After closing any running instance, copy the publish output over
`../../outputs/DiscordChatExporter-user-copy/`. Note this folder is outside the repo; do not commit it.

- [ ] **Step 5: Commit (plan/docs only — build artifacts are git-ignored)**

```bash
git add docs/plans/2026-06-03-incremental-json-export.md docs/specs/2026-06-03-incremental-json-export-design.md
git commit -m "Add incremental JSON export plan and design docs"
```

---

## Self-Review

**1. Spec coverage:**
- Detect & pick existing export → Task 5 (file picker) + Task 6 (button). ✓
- Cutoff = exact last message ID → Task 1 (`LastMessageId`) used as exclusive `After` in Task 5. ✓
- Download everything after cutoff → Task 5 reuses `ChannelExporter`. ✓
- Merge into one growing file → Task 2 (`JsonExportMerger`) + Task 5. ✓
- Boundary-safe deletion handling → exclusive ID cursor (Task 5); old file untouched except append (Task 2). ✓
- JSON only / GUI only → all tasks. ✓
- Non-goals refused (reverse, partitioned) → Task 1 `IsChronological` + Task 5 refusals. ✓
- Atomic/no-corruption → Task 2 temp + `File.Replace` + `.bak`. ✓
- "Already up to date" → Task 5 `ChannelEmptyException` + zero-added paths. ✓
- Testing strategy → Tasks 1–2 unit tests; Task 7 suite + smoke. ✓
- Rebuilt win-x64 exe → Task 7. ✓

**2. Placeholder scan:** No TBD/TODO; all code blocks are complete. The only conditional fallback ("substitute a valid icon") is a concrete build-failure remedy, not a placeholder.

**3. Type consistency:** `JsonExportInfo` fields (`GuildId`, `ChannelId`, `Before`, `LastMessageId`, `MessageCount`, `IsChronological`) are produced in Task 1 and consumed with matching names/types in Task 5. `JsonExportInspector.InspectAsync` and `JsonExportMerger.MergeAsync` signatures match their call sites. `ExportRequest` constructor argument order matches `DiscordChatExporter.Core/Exporting/ExportRequest.cs` (guild, channel, outputPath, assetsDirPath, format, after, before, partitionLimit, messageFilter, isReverseMessageOrder, shouldFormatMarkdown, shouldDownloadAssets, shouldReuseAssets, locale, isUtcNormalizationEnabled). `PromptSingleFilePathAsync` defined in Task 3, called in Task 5. `ContinueExportCommand` generated in Task 5, bound in Task 6.

**4. Scope-reduction scan:** Mentions of "not supported" (reverse, partitioned) and "does not download media" are the **user-approved non-goals** from the spec, not silent downgrades. No unsanctioned reductions.

---

## Execution Handoff

Selection: 7 tasks (≥ 5) and a context window already heavy with codebase reading → **Subagent-Driven**.
