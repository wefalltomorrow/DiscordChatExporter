# Incremental JSON Export ("Continue export") — Design

**Date:** 2026-06-03
**Status:** Approved
**Target:** DiscordChatExporter GUI (Avalonia, .NET 10) + Core

## Summary

Add a GUI flow that **appends new messages to an existing JSON export** instead of
re-downloading a channel from scratch. The user picks a prior `.json` export, the app
determines where that export left off, fetches only messages newer than the cutoff, and
merges them into the same file so it grows over time.

## Scope

- A "Continue export…" action in the GUI that:
  1. Lets the user pick an existing DCE JSON export file.
  2. Detects the cutoff (the exact ID of the last message already in the file).
  3. Downloads everything after the cutoff.
  4. Merges the new messages into the same file (single growing file).
- Core logic that is unit-testable and reusable (so a CLI surface could adopt it later).

## Non-goals (explicitly out for v1)

- **Non-JSON formats** (HTML / CSV / Plain text). JSON only.
- **Retroactive reconciliation** of edits/deletions to messages that were *already*
  downloaded. This is a **boundary-safe append**: previously-archived messages are frozen.
  Messages deleted or edited before the cutoff remain as they were in the original file.
- **Asset/media download on continue.** New messages keep their remote Discord CDN URLs;
  the merge never touches the assets directory.
- **Partitioned exports** (`… [part N].json`) and **reverse-order exports** (`--reverse`,
  newest-first). These are **detected during pre-flight and refused** with a clear message,
  rather than silently producing a corrupt or duplicated result.
- **CLI command.** Core logic is kept reusable, but no CLI wiring this round.

## Key correctness property: deletions don't break continuation

The cutoff is the **exact snowflake ID** of the last message in the existing export, passed
to Discord's `after` query parameter. That parameter is **exclusive and ID-range based** —
Discord returns messages whose ID is strictly greater than the cursor, regardless of whether
the cursor message (or any neighbouring messages) still exist. Therefore:

- If the boundary message — or any messages near it — were deleted between runs, pagination
  still resumes from the correct point. **No duplicates, no gaps.**
- Already-archived messages are never re-fetched, so the original file content is preserved
  exactly.

This is why a date-derived cursor is **not** acceptable: `Snowflake.FromDate` loses the
sub-millisecond sequence bits and could re-include or skip messages sharing a timestamp. We
always use the real last-message ID read from the file.

## Architecture & data flow

```
[Continue export…]  (Dashboard button)
   │
   ├─► OS file picker (*.json, opens in last-used export directory)
   │
   ├─► JsonExportInspector.InspectAsync(path)              [Core, streaming, O(1) memory]
   │       returns { GuildId, ChannelId, Before?,
   │                 LastMessageId, MessageCount, IsChronological }
   │       validates the file is a DCE JSON export
   │
   ├─► Resolve live objects via DiscordClient
   │       Guild   = GetGuildAsync(GuildId)  (or Guild.DirectMessages for DM channels)
   │       Channel = GetChannelAsync(ChannelId)   (fresh, for up-to-date LastMessageId)
   │
   ├─► Build ExportRequest:
   │       After  = LastMessageId   (exact, exclusive cursor)
   │       Before = original dateRange.before (usually null)
   │       Format = Json
   │       Output = %TEMP%/dce-continue-<guid>.json
   │       Markdown / Locale / UTC = persisted "Last…" settings
   │       Partition = Null, Filter = Null, Media = off, Reverse = false
   │
   ├─► ChannelExporter.ExportChannelAsync(request)         [unchanged, full reuse]
   │       → writes NEW messages to the temp file
   │       → throws ChannelEmptyException when MayHaveMessagesAfter(cutoff) is false
   │
   ├─► If 0 new messages → notify "Already up to date.", delete temp, stop
   │
   ├─► JsonExportMerger.MergeAsync(existingPath, tempPath) [Core, streaming]
   │       token-pump existing → merged.tmp:
   │         copy guild / channel / dateRange verbatim
   │         refresh exportedAt = now
   │         write existing message elements, then new message elements
   │         write messageCount = N + M
   │       File.Replace(merged.tmp → existingPath, .bak)   (atomic)
   │
   └─► notify "Added M message(s). Total: N+M."
```

## New components (Core)

Location: `DiscordChatExporter.Core/Exporting/Continuation/`

### `JsonExportInspector`

Streams an existing DCE JSON export with `Utf8JsonReader` (no DOM load) and extracts the
metadata needed to continue.

```csharp
public sealed record JsonExportInfo(
    Snowflake GuildId,
    Snowflake ChannelId,
    Snowflake? Before,          // from dateRange.before, if any
    Snowflake LastMessageId,    // cutoff
    long MessageCount,
    bool IsChronological        // first.timestamp <= last.timestamp
);

public static class JsonExportInspector
{
    // Throws InvalidJsonExportException (a DiscordChatExporterException) if the file is not
    // a recognisable DCE JSON export, or has zero messages.
    public static ValueTask<JsonExportInfo> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
```

- Reads `guild.id`, `channel.id`, `dateRange.before` from the preamble.
- Walks the top-level `messages` array, tracking the **first** and **last** element's
  `id` and `timestamp`, plus the element count.
- `LastMessageId` = last element's `id`. `IsChronological` = first.timestamp ≤ last.timestamp.
- Validation failures (missing `guild`/`channel`/`messages`, empty array, non-DCE JSON)
  throw a non-fatal `DiscordChatExporterException` with a user-friendly message.

### `JsonExportMerger`

Merges the freshly-exported temp JSON (new messages only) into the existing file.

```csharp
public static class JsonExportMerger
{
    // Produces a single consolidated JSON file. Writes to a sibling temp file, then
    // atomically replaces 'existingFilePath' (with a .bak backup).
    // Returns the merged total message count.
    public static ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default);
}
```

Implementation: **token-pump** using `Utf8JsonReader` (over each input) and a single
`Utf8JsonWriter` (Indented, `UnsafeRelaxedJsonEscaping`, `SkipValidation` — matching
`JsonMessageWriter`):

1. Open `merged.tmp`. Begin the root object by pumping the existing file's tokens:
   - Copy `guild`, `channel`, `dateRange` property subtrees verbatim.
   - When the `exportedAt` property is encountered, write the property name and substitute
     the **new** `exportedAt` value, skipping the original scalar.
   - When the `messages` property's `StartArray` is reached, begin streaming its element
     objects straight through to the writer, counting them (N).
2. Without closing the array, open the temp file, skip to its `messages` `StartArray`, and
   pump each element object into the same array, counting them (M).
3. Write the array `EndArray`, then `"messageCount": N + M`, then the root `EndObject`.
4. Flush/close. `File.Replace(merged.tmp, existingFilePath, existingFilePath + ".bak")`.

Properties:
- **O(1) memory** — one JSON token in flight at a time; safe for very large exports.
- **Schema-correct new messages** — they were serialized by the real `JsonMessageWriter`,
  not re-implemented here.
- **Robust to unknown/future top-level fields** — the pump copies whatever it sees before
  `messages` verbatim.
- **Atomic** — the original is replaced only after `merged.tmp` is fully written.

## GUI changes

`DiscordChatExporter.Gui`:

- **`ViewModels/Components/DashboardViewModel.cs`**
  - New `[RelayCommand(CanExecute = nameof(CanContinueExport))] ContinueExportAsync()`.
  - `CanContinueExport() => !IsBusy && _discord is not null`.
  - Orchestrates: file pick → `JsonExportInspector` → resolve guild/channel → build
    `ExportRequest` (cutoff = `LastMessageId`) → `ChannelExporter.ExportChannelAsync` to temp
    → `JsonExportMerger.MergeAsync` → snackbar result. Reuses `_progressMuxer`,
    `_snackbarManager`, `_dialogManager`, and persisted `SettingsService` values.
  - Catches `ChannelEmptyException` ("Already up to date"), non-fatal
    `DiscordChatExporterException` (snackbar), and reverse/partitioned refusals.
- **`Framework/DialogManager.cs`** — add `PromptSingleFilePathAsync(fileTypes, defaultDir)`
  using `StorageProvider.OpenFilePickerAsync` (`FilePickerOpenOptions { AllowMultiple =
  false, FileTypeFilter = [JSON] }`), mirroring the existing `PromptSaveFilePathAsync` /
  `PromptDirectoryPathAsync` methods (return `TryGetLocalPath() ?? Path.ToString()`).
- **`Views/Components/DashboardView.axaml`** — a "Continue export…" button next to Export.
- **`Localization/LocalizationManager*.cs`** — new strings: button label, "Already up to
  date", success ("Added {0} message(s)"), and refusal messages. English required; other
  locales fall back to English if not translated.

### Detecting & picking ("detect existing downloads, have you pick it")

v1 uses the **OS file picker** filtered to `*.json`, defaulting to the last-used export
directory, as the "detect + pick" mechanism. A richer in-app "scan a folder and list prior
exports" browser is a deferred enhancement (would require persisting export locations).

### Settings carried into the continue run

Because the original file does not record every option, the continue run uses persisted
"Last…" settings for **markdown formatting**, **locale**, and **UTC normalization**, and
fixed values for the rest (`Format = Json`, `Partition = Null`, `Filter = Null`,
`Media = off`, `Reverse = false`). `After` = detected cutoff; `Before` = original
`dateRange.before`. A markdown/UTC mismatch versus the original only affects how *new*
messages are formatted and is cosmetic.

## Error handling

| Situation | Behaviour |
|---|---|
| File is not a DCE JSON export / corrupt | Friendly snackbar; no changes made. |
| Export has zero messages | Treated as "nothing to continue"; suggest a normal export. |
| Partitioned export detected | Refuse with explanation (non-goal v1). |
| Reverse-order export detected (`IsChronological == false`) | Refuse with explanation. |
| No new messages since cutoff | `ChannelEmptyException` → "Already up to date." |
| Crash/cancel mid-merge | Original preserved (temp + atomic `File.Replace` + `.bak`). |
| DM channel | Guild resolved as `Guild.DirectMessages` (no guild API call). |

## Testing strategy

Core unit tests (no network — pure file logic), in `DiscordChatExporter.Cli.Tests` or a new
Core test fixture, using JSON fixtures:

- `JsonExportInspector`: well-formed export → correct cutoff/count/order; empty array →
  throws; malformed/non-DCE JSON → throws; reverse-order fixture →
  `IsChronological == false`; reads `dateRange.before`.
- `JsonExportMerger`: append into multi-message file; append into empty-messages file;
  single existing + single new; large/indented fixture; result re-parses as valid JSON and
  `messageCount` equals the actual element count; `exportedAt` refreshed; `guild`/`channel`
  preserved; original `.bak` created and original replaced atomically.
- Round-trip: inspect(merge(A, B)) sees `LastMessageId` == last id of B and count ==
  countA + countB.

Existing network/token-gated specs are unaffected.

## Rollout / build

The deliverable is a rebuilt `DiscordChatExporter.exe` (GUI, `win-x64`) containing the
feature, replacing the copy under `outputs/DiscordChatExporter-user-copy/`. Build via the
existing `DiscordChatExporter.Gui` project (Release, net10.0, win-x64).

## Future enhancements (not in v1)

- HTML/CSV/Plain-text continuation (format-aware merge or companion files).
- In-app "scan & list prior exports" browser.
- Media download on continue (write assets next to the original file).
- Optional overlap re-scan window to reconcile edits/deletions near the boundary.
- CLI `--continue <path>` for scripted/scheduled incremental runs.
