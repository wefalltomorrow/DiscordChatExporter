# JSON → Everything Conversion + Conversion Tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Convert an existing JSON export into HTML/CSV/TXT/SQLite offline (no Discord), via a Conversion tab. New JSON exports embed a `conversionData` block for full-fidelity conversion; legacy JSON degrades gracefully.

**Architecture:** Deserialize JSON → `Message` objects (`JsonExportReader`), drive the existing writers through `MessageExporter` with an **offline** `ExportContext` (`ExportConverter`). Never call `ChannelExporter`/network. `JsonMessageWriter` gains a trailing `conversionData` block; `ExportContext` gains a cache-seeding method. GUI tab mirrors the Library page.

**Tech Stack:** C# / .NET 10, System.Text.Json, Microsoft.Data.Sqlite, Avalonia, CommunityToolkit.Mvvm. Spec: `docs/specs/2026-06-05-conversion-tool-design.md`.

**Schema source of truth:** `DiscordChatExporter.Core/Exporting/JsonMessageWriter.cs` defines the exact JSON shape — the deserializer in Task 2 MUST mirror it field-for-field. Read its `WritePreambleAsync` (guild/channel/top-level), `WriteMessageAsync` (per-message), and the `Write*Async` helpers (author/attachment/embed/reaction/sticker/reference).

**Build/test:** `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~Conversion"` (token-free). `TreatWarningsAsErrors=true`, CSharpier-on-build. Implement **SQLite resume (the other plan) first** — its `SqliteExportReader` is reused in conversion tests.

---

### Task 1: `ConversionData` model + JSON writer enrichment

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Conversion/ConversionData.cs`
- Modify: `DiscordChatExporter.Core/Exporting/JsonMessageWriter.cs` (`WritePostambleAsync`)
- Test: `DiscordChatExporter.Cli.Tests/Specs/Conversion/JsonConversionDataSpecs.cs`

- [ ] **Step 1:** Create the model:

```csharp
using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Resolved lookups embedded at the end of a JSON export so it can be converted to other formats
// offline with full mention/color fidelity. Additive + versioned; legacy exports lack it.
public sealed record ConversionData(
    IReadOnlyList<ConversionMember> Members,
    IReadOnlyList<ConversionRole> Roles,
    IReadOnlyList<ConversionChannel> Channels
)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ConversionMember(
    string Id,
    string DisplayName,
    string? AvatarUrl,
    string? ColorHex,
    IReadOnlyList<string> RoleIds
);

public sealed record ConversionRole(string Id, string Name, string? ColorHex, int Position);

public sealed record ConversionChannel(string Id, string Name);
```

- [ ] **Step 2:** Read `JsonMessageWriter.WritePostambleAsync` and the `ExportContext` caches
  (`_membersById`, `_rolesById`, `_channelsById` — all private; add internal read accessors on
  `ExportContext` if needed, e.g. `internal IEnumerable<KeyValuePair<Snowflake, Member?>> CachedMembers => _membersById;`). In `WritePostambleAsync`, before closing the root object, write a
  `"conversionData"` object with `"schemaVersion": 1` and `members`/`roles`/`channels` arrays built
  from those caches (member: id, displayName=`DisplayName`, avatarUrl, color=`...ToHexString()`,
  roleIds; role: id, name, color, position; channel: id, name). Use the existing `_writer`
  (`Utf8JsonWriter`). **Failing test first:** export a synthetic channel (offline `ExportContext`,
  pattern from `SqliteMessageWriterSpecs`) where a member/role is seeded, assert the produced JSON text
  contains `"conversionData"` and the seeded display name + color.

- [ ] **Step 3:** Implement; **Step 4:** run test → pass; **Step 5:** commit
  `"Conversion: ConversionData model + JSON conversionData block"`.

---

### Task 2: `JsonExportReader` (deserializer)

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Conversion/JsonExportReader.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Conversion/JsonExportReaderSpecs.cs`

- [ ] **Step 1: Failing round-trip test** — write a JSON export with the real `JsonMessageWriter`
  (offline context; include a message with an attachment, a reaction, and a mention), then:

```csharp
var parsed = await JsonExportReader.ParseAsync(jsonPath);
parsed.Guild.Id.Should().Be(new Snowflake(1));
parsed.Channel.Id.Should().Be(new Snowflake(2));
parsed.Messages.Should().HaveCount(2);
parsed.Messages[0].Content.Should().Be("the quick brown fox");
parsed.Messages[0].Author.Id.Should().Be(new Snowflake(10));
parsed.Messages[0].Attachments.Should().ContainSingle();
```

  Plus: a non-DCE JSON (`{"hello":1}`) and a malformed file each throw `InvalidExportException`; a JSON
  with a `conversionData` block yields non-null `parsed.ConversionData` while one without yields null.

- [ ] **Step 2:** Run → fail (`JsonExportReader` not found).

- [ ] **Step 3: Implement.** Public surface:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
// InvalidExportException is in DiscordChatExporter.Core.Exporting.Continuation
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Core.Exporting.Conversion;

public sealed record ParsedExport(
    Guild Guild,
    Channel Channel,
    IReadOnlyList<Message> Messages,
    ConversionData? ConversionData
);

public static class JsonExportReader
{
    public static ValueTask<ParsedExport> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default
    );
}
```

  Implementation requirements (mirror `JsonMessageWriter` exactly — read it for the precise field
  names and nesting):
  - Open the file; parse with `System.Text.Json` (`JsonDocument.ParseAsync` for whole-file is fine for
    typical export sizes; if memory is a concern use `Utf8JsonReader` streaming). Wrap IO/JSON errors
    (`IOException`, `JsonException`, `UnauthorizedAccessException`) in `InvalidExportException`.
  - Validate it is a DCE export: require top-level `guild`, `channel`, `messages` — else throw
    `InvalidExportException("This file is not a DiscordChatExporter JSON export.")`.
  - Build `Guild` and `Channel` from their JSON objects (match the constructors used in the test
    fixtures / `SqliteMessageWriterSpecs.CreateContext`: `new Guild(Snowflake, name, iconUrl)`,
    `new Channel(Snowflake, ChannelKind, Snowflake guildId, parent, name, position, ..., topic, ...)` —
    read the actual constructors in `Core/Discord/Data/Guild.cs` and `Channel.cs`).
  - For each element of `messages[]`, reconstruct a `Message` (read `Core/Discord/Data/Message.cs` for
    the constructor) and its nested `Author` (`User`), `Attachments`, `Embeds`, `Stickers`,
    `Reactions` (`Emoji` + count), `MentionedUsers`, `Reference`. Parse `Snowflake` via
    `new Snowflake(ulong.Parse(idText))`, timestamps via `DateTimeOffset.Parse(..., RoundtripKind)`,
    colors via the project's hex parser (see `Color`/`ToHexString` usage).
  - Parse the optional trailing `conversionData` object into `ConversionData` (null if absent).
  - Return `new ParsedExport(guild, channel, messages, conversionData)`.

  NOTE: this is the largest task. Because the JSON schema has many nested optional shapes, implement it
  incrementally against the round-trip test, adding fields until the test's assertions (and a couple
  more for embeds/mentions) pass. Keep it tolerant of missing optional keys.

- [ ] **Step 4:** Run → pass. **Step 5:** commit `"Conversion: JsonExportReader (JSON -> Message)"`.

---

### Task 3: `ExportContext.SeedFromConversionData`

**Files:**
- Modify: `DiscordChatExporter.Core/Exporting/ExportContext.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Conversion/ExportContextSeedSpecs.cs`

- [ ] **Step 1: Failing test** — build an `ExportContext` (offline), `SeedFromConversionData(data)`
  with a member that has a display name + color and a role, then assert `context.TryGetMember(id)` is
  non-null with that display name and `context.TryGetUserColor(id)` returns the color.

- [ ] **Step 2:** Run → fail.

- [ ] **Step 3: Implement** a public method on `ExportContext` (it's `internal`, fine):

```csharp
// Seed the member/role/channel caches from a JSON export's conversionData block so the writers can
// resolve mentions/colors offline (no Discord). Mirrors what PopulateChannelsAndRolesAsync /
// PopulateMemberAsync would have produced from the network.
public void SeedFromConversionData(Conversion.ConversionData data)
{
    foreach (var role in data.Roles)
        _rolesById[new Snowflake(ulong.Parse(role.Id, CultureInfo.InvariantCulture))] =
            /* construct Role: read Core/Discord/Data/Role.cs ctor — id, name, Color?, position */;

    foreach (var channel in data.Channels)
        _channelsById[new Snowflake(ulong.Parse(channel.Id, CultureInfo.InvariantCulture))] =
            /* construct minimal Channel or store name-bearing channel — read Channel.cs */;

    foreach (var member in data.Members)
        _membersById[new Snowflake(ulong.Parse(member.Id, CultureInfo.InvariantCulture))] =
            /* construct Member: read Core/Discord/Data/Member.cs (and Member.CreateFallback) */;
}
```

  Read `Role.cs`, `Member.cs`, `Channel.cs`, and the `Color` parse helper for the exact constructors;
  parse `ColorHex` with the same helper `ToHexString()` round-trips. Add `using System.Globalization;`
  if missing.

- [ ] **Step 4:** Run → pass. **Step 5:** commit `"Conversion: ExportContext.SeedFromConversionData"`.

---

### Task 4: `ExportConverter` orchestrator

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Conversion/ExportConverter.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/Conversion/ExportConverterSpecs.cs`

- [ ] **Step 1: Failing tests** — convert a JSON fixture to SQLite and assert via `SqliteExportReader`
  there is a search hit for a known message; convert to CSV and assert the output file contains a
  known message's content:

```csharp
var dbOut = Path.Combine(_dir, "out.db");
await ExportConverter.ConvertAsync(jsonPath, dbOut, ExportFormat.Db);
(await SqliteExportReader.SearchAsync(dbOut, "quick", 50, default)).Should().ContainSingle();

var csvOut = Path.Combine(_dir, "out.csv");
await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);
(await File.ReadAllTextAsync(csvOut)).Should().Contain("quick brown fox");
```

- [ ] **Step 2:** Run → fail.

- [ ] **Step 3: Implement** (mirror `ExportRequest` construction from
  `DashboardViewModel.BuildExportRequest` / `SqliteMessageWriterSpecs.CreateContext`; `MessageExporter`
  is `internal` in the same assembly):

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Core.Exporting.Conversion;

public static class ExportConverter
{
    public static async ValueTask<ExportResult> ConvertAsync(
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken cancellationToken = default
    )
    {
        var parsed = await JsonExportReader.ParseAsync(jsonFilePath, cancellationToken);

        var request = new ExportRequest(
            parsed.Guild,
            parsed.Channel,
            outputFilePath,
            null,                       // assetsDirPath
            targetFormat,
            null,                       // after
            null,                       // before
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false, // keep the JSON's URLs as-is (non-lossy, offline)
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

        // Dummy client is never called — conversion never touches the network.
        var context = new ExportContext(new DiscordClient("conversion-offline"), request);
        if (parsed.ConversionData is not null)
            context.SeedFromConversionData(parsed.ConversionData);

        // Mirror ChannelExporter: dispose (which runs the writer postamble / forces an empty file)
        // in finally, THEN read Files — the stats aren't final until after disposal.
        var exporter = new MessageExporter(context);
        try
        {
            foreach (var message in parsed.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await exporter.ExportMessageAsync(message, cancellationToken);
            }
        }
        finally
        {
            await exporter.DisposeAsync();
        }

        return new ExportResult(exporter.Files, exporter.MessagesExported, 0);
    }
}
```

  (If `MessageExporter`/`ExportResult` accessibility blocks this, they are `internal` in
  `DiscordChatExporter.Core.Exporting` — same namespace/assembly, so no change needed. The empty-file
  behavior on dispose mirrors `ChannelExporter`.)

- [ ] **Step 4:** Run → pass. **Step 5:** commit `"Conversion: ExportConverter (offline JSON -> any format)"`.

---

### Task 5: GUI Conversion tab

**Files (mirror the Library page precedent exactly):**
- Create: `DiscordChatExporter.Gui/ViewModels/Components/ConversionViewModel.cs`
- Create: `DiscordChatExporter.Gui/Views/Components/ConversionView.axaml` + `.axaml.cs`
- Modify: `Gui/Framework/ViewManager.cs` (add `ConversionViewModel => new ConversionView()`)
- Modify: `Gui/Framework/ViewModelManager.cs` (add `GetConversionViewModel()`)
- Modify: `Gui/App.axaml.cs` (add `services.AddTransient<ConversionViewModel>()`)
- Modify: `Gui/ViewModels/MainViewModel.cs` (wire `Dashboard.ConversionRequested += (_,_) => ShowConversion();` in the constructor; add `ShowConversion()` that creates the VM, subscribes `BackRequested → CurrentPage = Dashboard`, sets `CurrentPage`)
- Modify: `Gui/ViewModels/Components/DashboardViewModel.cs` (add `public event EventHandler? ConversionRequested;` and `[RelayCommand] private void NavigateToConversion() => ConversionRequested?.Invoke(this, EventArgs.Empty);`)
- Modify: `Gui/Views/Components/DashboardView.axaml` (add a button/menu entry bound to `NavigateToConversionCommand`, next to the Library entry)
- Modify: `Gui/Localization/LocalizationManager.cs` + `.English.cs` (any new UI strings)
- Test: `DiscordChatExporter.Gui.Tests/ConversionViewRenderTests.cs` (optional headless render-smoke, mirror `LibraryViewRenderTests`)

- [ ] **Step 1:** `ConversionViewModel`: `BackRequested` event + `NavigateBack` command; observable
  props for selected source JSON file paths (`ObservableCollection<string>`), the target-format
  toggles (HTML Dark, HTML Light, CSV, TXT, SQLite), the output folder, `IsBusy`, and a results
  collection. Commands: `PickFilesCommand` (`DialogManager.PromptMultipleFilePathsAsync` or single,
  *.json filter), `PickOutputFolderCommand` (`PromptDirectoryPathAsync`), `ConvertCommand` (loop each
  source × each selected format → `ExportConverter.ConvertAsync(src, Path.Combine(outDir,
  baseName + "." + format.GetFileExtension()), format)`, catching per item, appending a result row;
  surface whether each source had `conversionData`). Check `DialogManager` for the exact file-picker
  helper names (`PromptSingleFilePathAsync` exists; add a multi-file variant if needed).
- [ ] **Step 2:** `ConversionView.axaml` — Material-styled page (mirror `LibraryView.axaml`): header
  with title + Back, a "Pick JSON files" button + list, format checkboxes, output-folder picker, a
  Convert button, a results list, and a busy indicator.
- [ ] **Step 3:** Wire `ViewManager`, `ViewModelManager`, `App.axaml.cs`, `MainViewModel`,
  `DashboardViewModel`, `DashboardView.axaml` per the file list above.
- [ ] **Step 4:** Build the GUI → 0 warnings/0 errors. Optional: add the render-smoke test.
- [ ] **Step 5:** commit `"Conversion: GUI Conversion tab + navigation"`.

---

### Task 6: Whole-feature verification + deploy

- [ ] **Step 1** — `dotnet build DiscordChatExporter.slnx` → 0/0.
- [ ] **Step 2** — `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~Conversion"` → all pass.
- [ ] **Step 3** — `dotnet test DiscordChatExporter.Gui.Tests` → no regression.
- [ ] **Step 4** — trimmed self-contained publish (`dotnet publish DiscordChatExporter.Gui -c Release -r win-x64 --self-contained`) succeeds; verify only the pre-existing Material.Avalonia trim warnings appear (no IL2026 from the new System.Text.Json deserializer — if any appear, add a source-generated `JsonSerializerContext` or keep to `Utf8JsonReader`/`JsonDocument` which are trim-safe).
- [ ] **Step 5** — redeploy over the user-copy, preserving `Settings.dat`.
