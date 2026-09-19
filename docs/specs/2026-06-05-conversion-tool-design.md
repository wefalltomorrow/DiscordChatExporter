# JSON → Everything Conversion + Conversion Tab — Design

**Status:** Approved (2026-06-05). Feature ② of the two-feature batch (feature ① = SQLite resume,
`docs/specs/2026-06-05-sqlite-resume-design.md`). Implemented after ①.

## Goal

A **Conversion** tab that converts an existing **JSON** export into any other lossless target format
(**HTML Dark/Light, CSV, TXT, SQLite**) **offline — no Discord connection, no token**. All message
data is preserved. New JSON exports embed a trailing **`conversionData`** block (the resolved
member/role/channel lookups already held at export time) so they convert with full fidelity; legacy
JSON without the block converts best-effort (degrading gracefully to message-embedded author names and
default colors).

## Why JSON is the only source

JSON is the richest format — a superset of what every other writer needs (it alone keeps full embeds
and per-emoji reaction data). So JSON → anything is lossless, while HTML/SQLite → JSON would be lossy
or brittle. This feature converts **from JSON only**.

## Key architectural insight

The export pipeline is `Discord → file`, but the network is only touched by `ChannelExporter`
(`PopulateChannelsAndRolesAsync`) and by `ExportContext.ResolveAssetUrlAsync` *when*
`ShouldDownloadAssets` is true. The **writers themselves never call the network** — they only read
the in-memory `Message` objects and the `ExportContext` caches (which degrade gracefully when empty).
So conversion = **deserialize JSON → `Message` objects → drive the existing writers via
`MessageExporter` with an offline `ExportContext`**, never calling `ChannelExporter` and never
populating from Discord. `MessageExporter` (not `ChannelExporter`) is the right entry point: it owns
the writer lifecycle + partitioning and has no network dependency.

## Architecture

New namespace `DiscordChatExporter.Core.Exporting.Conversion`. Four Core units + one `ExportContext`
change + the GUI tab.

### `ConversionData` (new model) + writer enrichment

A small record persisted at the **end** of every new JSON export, carrying the resolved lookups the
writers would otherwise need a live Discord connection to rebuild:

```
ConversionData(
    IReadOnlyList<ConversionMember> Members,   // id, displayName, avatarUrl, colorHex?, roleIds[]
    IReadOnlyList<ConversionRole>   Roles,      // id, name, colorHex?, position
    IReadOnlyList<ConversionChannel>Channels    // id, name
)
```

- `JsonMessageWriter.WritePostambleAsync` emits a `"conversionData"` JSON object (versioned:
  `"schemaVersion": 1`) built from the `ExportContext` caches that were populated during the export.
  Source of truth: `ExportContext._membersById` / `_rolesById` / `_channelsById`. This is **additive**
  — older readers ignore unknown keys, and legacy exports simply lack it.
- This is the *only* change to existing export behavior, and it is backward-compatible.

### `JsonExportReader` (new)

```csharp
public static class JsonExportReader
{
    public static ValueTask<ParsedExport> ParseAsync(string filePath, CancellationToken ct = default);
}

public sealed record ParsedExport(
    Guild Guild,
    Channel Channel,
    IReadOnlyList<Message> Messages,
    ConversionData? ConversionData
);
```

- Deserializes a DCE JSON export back into the Core domain objects (`Guild`, `Channel`, `Message`
  with its `Author`, `Attachments`, `Embeds`, `Stickers`, `Reactions`, `MentionedUsers`, `Reference`,
  etc.). **It must mirror `JsonMessageWriter` field-for-field** — that writer is the schema's source of
  truth (top-level `guild`/`channel`/`messages[]`; per-message shape in its `WriteMessageAsync`).
- Parses the trailing `conversionData` block if present (`null` for legacy exports).
- Throws `InvalidExportException` (reuse the existing one in `Core.Exporting.Continuation`) on missing
  file, malformed JSON, or a JSON that isn't a DCE export (no `guild`/`channel`/`messages`).
- Uses `System.Text.Json` (`JsonDocument`/`Utf8JsonReader`), consistent with the codebase.

### `ExportContext` offline seeding (modify)

`ExportContext`'s caches are private and only filled by the network `Populate*` methods. Add an
internal seeding entry point so conversion can pre-fill them from a `ConversionData` block:

```csharp
// In ExportContext (internal): seed the member/role/channel caches without any network calls.
public void SeedFromConversionData(ConversionData data) { /* fill _membersById/_rolesById/_channelsById */ }
```

When `ConversionData` is null (legacy JSON), the caches stay empty and the writers fall back to each
`Message`'s embedded author data (`TryGetMember(id)?.DisplayName ?? user.DisplayName`,
`TryGetUserColor` → null → default) — i.e. best-effort, no crash.

### `ExportConverter` (new) — orchestrator

```csharp
public static class ExportConverter
{
    public static ValueTask<ExportResult> ConvertAsync(
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken ct = default
    );
}
```

Flow (no network at any point):
1. `parsed = await JsonExportReader.ParseAsync(jsonFilePath, ct)`.
2. Build an offline `ExportRequest` from `parsed.Guild` + `parsed.Channel`: `Format = targetFormat`,
   `OutputFilePath = outputFilePath`, `ShouldDownloadAssets = false` (so `ResolveAssetUrlAsync`
   returns the JSON's stored URLs unchanged — assets stay non-lossy), `PartitionLimit.Null`,
   `MessageFilter.Null`, markdown/UTC flags carried from sensible defaults.
3. `var context = new ExportContext(new DiscordClient("conversion-offline"), request)` (the dummy
   client is never called — same trick the token-free specs use). If `parsed.ConversionData` is not
   null, `context.SeedFromConversionData(parsed.ConversionData)`.
4. `await using var exporter = new MessageExporter(context); foreach (var m in parsed.Messages) await
   exporter.ExportMessageAsync(m, ct);` — the writer's preamble/postamble run via the exporter's
   lifecycle (postamble on dispose), partitioning honored. Never call `ChannelExporter` /
   `PopulateChannelsAndRolesAsync`.
5. Return the `ExportResult` (`exporter.Files`, `exporter.MessagesExported`, asset count 0).

`MessageExporter` is `internal partial` in `Core.Exporting`; `ExportConverter` is in the same assembly
so it can use it directly.

### GUI — Conversion tab

Follows the **Library page pattern exactly** (the most recent precedent):

- `ConversionViewModel` (`Gui/ViewModels/Components/`) + `ConversionView.axaml(.cs)` (`Gui/Views/Components/`).
- Register: `ViewManager.TryCreateView` (`ConversionViewModel => new ConversionView()`),
  `ViewModelManager.GetConversionViewModel()`, `App.axaml.cs` (`AddTransient<ConversionViewModel>()`).
- Navigation: `DashboardViewModel` gets a `ConversionRequested` event + `[RelayCommand]
  NavigateToConversion`; `MainViewModel` wires `Dashboard.ConversionRequested += ShowConversion()` in
  its constructor (next to the existing Library wiring) and `ShowConversion()` swaps `CurrentPage`,
  subscribing the VM's `BackRequested` to return to the Dashboard. Add a toolbar/FAB entry on the
  Dashboard to raise `ConversionRequested` (mirror the Library button).
- UX: pick one or more source **JSON** files; choose target format(s) — HTML Dark, HTML Light, CSV,
  TXT, SQLite — (multi-select so a JSON can be fanned out to several at once); choose an output
  folder; **Convert** button → runs `ExportConverter.ConvertAsync` per (file × format); shows progress
  and a per-item result; surfaces whether each source has a `conversionData` block (full-fidelity) or
  not (best-effort). Output file name = source base name + the target's extension, written to the
  chosen folder.

## Data flow

```
pick chat.json + targets {HTML, SQLite}
  → JsonExportReader.ParseAsync → ParsedExport(Guild, Channel, Messages, ConversionData?)
  → for each target format:
      ExportConverter.ConvertAsync(chat.json, <out>/chat.<ext>, format)
        → offline ExportRequest + ExportContext (seed caches if ConversionData present)
        → MessageExporter loop over Messages → writer → <out>/chat.<ext>
```

## Fidelity

- **Data:** lossless — content, embeds, attachments, reactions, mentions, stickers all come from JSON.
- **Mentions / role colors:** full when `conversionData` is present (new exports); best-effort for
  legacy JSON (message-embedded display names, default colors). Never any network.
- **Assets:** URLs preserved exactly as stored in the JSON (no re-download).
- **SQLite target:** inherits the `.db` format's intentional reductions (reaction counts only, no
  embeds table) — expected, since that's what the SQLite format is.

## Error handling

- Missing / malformed / non-DCE JSON → `InvalidExportException` → standard GUI error message.
- Batch (file × format): a single failure reports against that item and does not abort the rest.
- Output collision: if the target file already exists, overwrite (the writer/SQLite already
  clean-slate; HTML/CSV/TXT truncate via `File.Create`).

## Testing strategy (token-free)

Tests in `DiscordChatExporter.Cli.Tests/Specs/Conversion/`, building fixtures by exporting synthetic
messages to JSON with the real `JsonMessageWriter` + offline `ExportContext` (same pattern as
`SqliteMessageWriterSpecs`):

- **`JsonExportReader` round-trip:** write a JSON export with N messages (author, attachment,
  reaction, embed, a mention) → `ParseAsync` → assert the `Message` objects' ids/content/author/
  attachment/reaction/embed fields match the originals.
- **`conversionData` round-trip:** export with seeded members/roles → assert the JSON contains the
  block and `JsonExportReader` reads it back; a legacy JSON (block stripped) → `ConversionData == null`.
- **`ExportConverter` JSON → SQLite:** convert → open the `.db` with `SqliteExportReader`/raw SQL →
  assert message rows + FTS hit.
- **`ExportConverter` JSON → CSV / TXT / HTML:** convert → assert the output file exists and contains a
  known message's content.
- **Best-effort (legacy):** convert a JSON without `conversionData` → no throw, content present.

GUI tab gets one headless render-smoke test (mirroring `LibraryViewRenderTests`) if feasible; the
conversion logic itself is fully covered token-free in Core.

## Scope / non-goals

- Source is **JSON only**. No HTML/CSV/SQLite → other (lossy/brittle).
- No asset re-download (URLs preserved; a future `ShouldDownloadAssets` conversion mode is out of scope).
- No schema migration for the parsed JSON beyond tolerating missing/unknown keys.
- `conversionData` is additive + versioned; it never breaks existing exports or older app versions.
