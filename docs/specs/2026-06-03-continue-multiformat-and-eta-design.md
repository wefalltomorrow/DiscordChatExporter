# Continue Export: HTML + CSV support, and Export ETA — Design

**Date:** 2026-06-03
**Status:** Approved (pending spec review)
**Target:** DiscordChatExporter GUI (Avalonia, .NET 10) + Core
**Builds on:** `2026-06-03-incremental-json-export-design.md` (the JSON-only "Continue export" feature)

## Summary

Two independent enhancements to the existing GUI "Continue export" feature and the export UI:

1. **More formats for Continue export** — extend continuation from JSON-only to also support
   **HTML** and **CSV**. (Plain-text/TXT is explicitly *not* supported — see Non-goals.)
2. **Estimated time to completion (ETA)** — show a live "time remaining" estimate next to the
   dashboard progress bar during any export *and* during continue.

## Part A — Multi-format Continue export

### Scope
- Add HTML and CSV to the formats that can be continued. JSON already works.
- Keep the same flow: pick an existing export file → determine the cutoff → resolve the live
  channel → export only newer messages (same format) to a temp file → merge into the original.
- Dispatch on the file's extension to a per-format handler.

### Non-goals (Part A)
- **Plain-text (.txt) continuation.** TXT message headers carry only a minute-precision,
  locale-formatted timestamp (`FormatDate` default `"g"`) and no message ID, so a reliable
  cutoff is not recoverable. Picking a `.txt` file is **refused** with a clear message.
- **Partitioned exports** (`… [part N].ext`) — detected by name/sibling and refused (as in JSON v1).
- **Reverse-ordered exports** — detected (first vs last cutoff) and refused.
- **Media re-download on continue** — unchanged from v1 (off; new messages keep remote URLs).
- **Renamed/custom-named files** that lack the `[<channelId>]` token AND lack an in-file channel
  id (CSV/TXT) — refused with guidance to keep the default filename or re-export.

### Cutoff & channel-identity reality (why this differs per format)

| Format | Message ID in file? | Channel/Guild id in file? | Cutoff precision |
|---|---|---|---|
| JSON | yes (`id`) | yes (`channel.id`, `guild.id`) | exact |
| HTML | yes (`data-message-id="…"`) | no (names only) | exact |
| CSV  | no (cols: `AuthorID,Author,Date,Content,Attachments,Reactions`) | no | ISO `"o"` timestamp (~ms) |

**Channel identity:** CSV (and HTML) do not embed a channel id. The universal source is the
**filename**, which by default embeds `[<channelId>]` (e.g. `Guild - general [12345].csv`,
produced by `ExportRequest.GetDefaultOutputFileName`). Resolution order:
1. Parse `[<digits>]` from the filename (all formats).
2. Fallback to in-file `channel.id` for JSON/HTML.
3. Neither found → refuse with guidance.

Guild id is **not** needed from the file: `GetChannelAsync(channelId)` returns the channel +
`GuildId`; then `GetGuildAsync(channel.GuildId)` (or `Guild.DirectMessages` if `channel.IsDirect`).

**Cutoff value:**
- JSON / HTML → exact last message-id snowflake → exclusive `after` cursor → **no dupes/gaps**,
  immune to deleted messages (same guarantee as JSON v1).
- CSV → last data row's `Date` (ISO `"o"`, culture-invariant, ~ms) → `Snowflake.FromDate`. Because
  `FromDate` truncates to the millisecond, the boundary message can be re-fetched; the CSV merge
  therefore **skips appended rows whose timestamp ≤ the cutoff timestamp** to remove the ≤1
  boundary duplicate. (Rare same-millisecond ambiguity is accepted — CSV has no id to disambiguate.)

### Architecture

A light dispatcher selects a per-format handler by extension. Existing JSON code is reused
unchanged; two new format handlers are added, plus shared helpers.

```
Core/Exporting/Continuation/
  JsonExportInspector.cs      (existing) — exact id cutoff from JSON
  JsonExportMerger.cs         (existing) — token-pump JSON merge
  CsvExportInspector.cs       (new)      — last-row timestamp cutoff from CSV
  CsvExportMerger.cs          (new)      — append rows (skip header + rows ≤ cutoff)
  HtmlExportInspector.cs      (new)      — last data-message-id cutoff from HTML
  HtmlExportMerger.cs         (new)      — splice message groups + bump count
  ContinuationCutoff.cs       (new)      — shared record returned by all inspectors
  FileNameChannelId.cs        (new)      — parse [<id>] from a filename
  ContinuationFormat.cs       (new)      — extension → handler dispatch; resolve cutoff + merge
```

`ContinuationCutoff` (shared):
```csharp
public sealed record ContinuationCutoff(
    Snowflake ChannelId,
    Snowflake Cutoff,        // exact message id (JSON/HTML) or FromDate(lastTimestamp) (CSV)
    Snowflake? Before,       // carried forward if the original had a 'before' bound
    bool IsChronological,
    long ExistingCount,
    bool CutoffIsExact       // true for JSON/HTML, false for CSV (drives boundary skipping)
);
```

`ContinuationFormat` exposes:
```csharp
public static bool IsSupportedExtension(string filePath);   // .json/.html/.htm/.csv
public static ValueTask<ContinuationCutoff> ReadCutoffAsync(string filePath, CancellationToken);
public static ValueTask<long> MergeAsync(string existingPath, string newPath,
    ContinuationCutoff cutoff, DateTimeOffset exportedAt, CancellationToken); // returns total
```
JSON dispatch wraps the existing `JsonExportInspector`/`JsonExportMerger` (mapping `JsonExportInfo`
→ `ContinuationCutoff` with `CutoffIsExact = true`, `ChannelId` from in-file or filename).

### Per-format details

**CSV** (`CsvExportInspector` / `CsvExportMerger`)
- Inspect: read the file; header is line 1. Parse the **first** and **last** data rows' `Date`
  column (ISO `"o"` via `DateTimeOffset.Parse(..., RoundtripKind, InvariantCulture)`). Cutoff =
  `FromDate(lastDate)`; `IsChronological` = firstDate ≤ lastDate; `ExistingCount` = data-row count;
  `ChannelId` from filename. CSV rows can contain quoted, embedded newlines — parse with a minimal
  RFC-4180-aware row splitter (quotes doubled, commas/newlines inside quotes), not naive line split.
- Merge: append the temp export's data rows (everything after its header line), **skipping rows
  whose `Date` ≤ cutoff timestamp**. No header rewrite needed (CSV has no footer/count). Write via
  temp + atomic `File.Replace` + `.bak` (same safety as JSON).

**HTML** (`HtmlExportInspector` / `HtmlExportMerger`) — hardened marker splice into one file.
This section reflects a 5-agent design review; see "HTML merge — alternatives considered" below.

*Verified minifier behavior* (`new HtmlMinifier()`, defaults, WebMarkupMin 2.21.0 — confirmed
empirically): `RemoveHtmlComments = true` (the templates' `<!--wmm:ignore-->` directive comments
are **stripped**, but the content they wrap is preserved verbatim — so do NOT anchor on comments);
`AttributeQuotesRemovalMode = Html5` (quotes dropped on single-token values: `data-message-id=123`,
`class=postamble`; kept on multi-token values like `class="a b"`); attributes are **not reordered**;
`RemoveOptionalEndTags = true` (so `</body>`/`</html>` **may be absent** — never anchor on them).
All regexes are quote-tolerant (e.g. `class="?postamble"?`, `data-message-id="?(\d+)"?`).

- **Inspect:** scan `data-message-id="?(\d+)"?`; first/last give order + the **exact** cutoff id;
  `ExistingCount` = id count; `ChannelId` from filename (HTML preamble has no channel id).
  `CutoffIsExact = true`. Refuse reverse order (first id > last id).
- **Merge (string splice, anchored on stable class tokens):**
  1. Splice point in the existing file: find the `<div class="?postamble"?>` opener (use the **last**
     occurrence), then the `</div>` immediately preceding it (the chatlog-container close). Insert
     before that `</div>`. (`</div>` alone is not unique — always locate the postamble first.)
  2. Extract new groups from the temp export: the slice between `<div class="chatlog">` (the
     container open, preserved verbatim) and that same postamble-preceding `</div>`. This is exactly
     the `chatlog__message-group` blocks — no preamble/theme CSS, no postamble.
  3. **Dedupe** the temp slice against the existing file by `data-message-id` (drop any new message
     whose id already appears — guards overlap/boundary).
  4. Insert the deduped slice at the splice point.
  5. **Recompute** the `Exported N message(s)` count = total `data-message-id` count in the merged
     file, and rewrite the postamble count entry (anchor on the invariant English wrapper
     `Exported …​ message(s)`, replace the inner number, formatted `n0` with the export culture).
     Do **not** parse the old localized number (grouping separators include U+202F, U+066C). If the
     entry can't be matched, leave it unchanged (cosmetic) rather than corrupt the file.
  6. Write to a temp file; **validate before committing** (next bullet); then atomic `File.Replace`
     + `.bak`.
- **Validate-before-commit gate** (abort the replace, keep the original, on any failure): merged
  contains exactly one `<div class="chatlog">` and exactly one `<div class="?postamble"?>`; the set
  of `data-message-id`s = old ∪ new with **no duplicates** and in **ascending** order; merged length
  > original length. (Atomic replace prevents corruption; this gate prevents a silently-wrong file.)
- **Injection-safe:** message content is `HtmlEncode`d at render, so a user message containing
  `<div class="postamble">` becomes `&lt;div…` and cannot forge the splice anchor.
- **Optional producer hardening (future files):** during normal HTML export, emit a stable sentinel
  at the chatlog-close boundary (a real element/attribute, or a comment registered via
  `PreservableHtmlCommentList="^dce:"`, since default settings strip plain comments). New exports
  then splice on an exact unique sentinel, immune to template/minifier drift. Legacy files (no
  sentinel) use the `<div class="postamble">` token path above. This is a low-priority enhancement;
  the token path is the universal mechanism and works without it.
- **Accepted cosmetic seam:** if the last existing message and the first new message share an author
  within 7 minutes, they render as two adjacent groups (a repeated author header) instead of one.
  Visual only; all content + `data-message-id`s + anchor links are intact. (A perfect seam-merge is
  not achievable anyway: grouping needs the old messages' precise timestamps, but rendered HTML
  carries only minute-precision display strings.)
- **Pre-implementation action (do not skip):** capture one **real** minified HTML export and confirm
  the exact seam bytes (quote presence on `class="postamble"`, attribute order, whether `</body>`
  survives) before finalizing the regexes. The shipped file contains no `wmm:ignore` comments.

**HTML merge — alternatives considered and rejected** (5-agent review):
- *Runtime DOM merge (AngleSharp):* AngleSharp is trim-viable (reflection-free `Configuration.Default`
  + `TrimmerRootAssembly` escape hatch), but **rejected**: a full parse+serialize **rewrites every
  byte of the growing file each continue** (re-quotes attributes / re-minifies differently than
  WebMarkupMin) and **normalizes whitespace inside `white-space: pre-wrap` spans and multiline code
  blocks → real content corruption**, with a memory curve that grows with the file an append feature
  is meant to grow cheaply. Its only edge — merging the seam group — is only approximate anyway.
- *Companion file (+ index):* simplest/safest, but **rejected**: `scrollToMessage` and reply/timestamp
  anchors (`#chatlog__message-container-{id}`) resolve within a **single document**, so a reply in a
  later file pointing at a message in an earlier file becomes a **dead link** — a functional
  regression, not just "more files." (Noted: partitioned exports and `--media` already produce
  sibling files, but those don't introduce cross-file *anchor* breakage.)

### GUI changes (Part A)
`DashboardViewModel.ContinueExportAsync` is generalized:
- File picker filter widens to `*.json;*.html;*.htm;*.csv` (label "Supported exports").
- After picking: if `!ContinuationFormat.IsSupportedExtension(path)` (e.g. `.txt`) → snackbar
  "Continuing {ext} exports isn't supported" and return.
- Replace the JSON-specific inspect/merge calls with `ContinuationFormat.ReadCutoffAsync` /
  `MergeAsync`. The temp export's `ExportFormat` is chosen to match the existing file's extension
  (`.json`→Json, `.html`/`.htm`→HtmlDark, `.csv`→Csv). HTML theme for the temp does not affect the
  merge (only message-group markup is spliced, not theme CSS).
- New localization strings: `ContinueExportFormatUnsupportedMessage`,
  `ContinueExportChannelUnknownMessage` (filename/in-file id not found).
- The existing partition/reverse/up-to-date handling stays.

### Testing (Part A)
Core unit tests (no network) with fixtures, per handler:
- `FileNameChannelId`: parses `[<id>]` from default names; returns null for names without it.
- `CsvExportInspector`: cutoff/order/count from a CSV; quoted-field + embedded-comma/newline rows.
- `CsvExportMerger`: append rows, header skipped, boundary rows (`≤ cutoff`) skipped, re-parse valid,
  `.bak` created.
- `HtmlExportInspector`: extracts last `data-message-id` (quoted and unquoted/minified), order from
  first/last, channel id from filename.
- `HtmlExportMerger` (most fixtures — riskiest merge): splices new groups before the postamble;
  count recomputed to N+M from the merged `data-message-id` count; the validate gate passes only
  when there's exactly one `<div class="chatlog">` + one postamble and all old∪new ids are present,
  ascending, de-duplicated. Cases: normal append; **overlapping** ids deduped; **0-message** existing
  export; single message / single group; quote-stripped (minified) input; non-English count culture
  (de-DE / fr-FR U+202F / Arabic U+066C) — count still rewritten correctly; a deliberately malformed
  merge result → gate **aborts**, original untouched. Fixtures derived from a **real captured minified
  export** (per the pre-implementation action), not hand-written guesses.
- Dispatch: `.txt` → unsupported; unknown channel id → refusal; reverse-ordered HTML → refused.

## Part B — Export ETA

### Scope
Show a live "time remaining" estimate beside the dashboard progress bar for normal exports and
continue. Reuses the existing muxed progress fraction (so multi-channel parallel exports get an
aggregate ETA).

### Approach
Discord exposes **no total-message count**, so ETA is derived from the progress **fraction** the
exporter already reports (`GetMessagesAsync` reports a `Gress.Percentage` based on where the current
message's timestamp sits between the first and last message in range).

`EtaEstimator` (Core or GUI util, pure + unit-testable):
```csharp
public sealed class EtaEstimator
{
    public void Report(double fraction, DateTimeOffset now);   // sample
    public TimeSpan? Estimate { get; }                          // null until confident
    public void Reset();
}
```
- Keeps a rolling window of `(fraction, time)` samples (default ~60s, capped count).
- `rate = (fractionNewest − fractionOldest) / (timeNewest − timeOldest)`; `Estimate =
  (1 − fractionNewest) / rate`.
- Returns `null` (UI shows "estimating…") until: window spans ≥ a few seconds AND fraction is in
  `(0,1)` AND rate > 0. Returns `TimeSpan.Zero`/hides at fraction ≥ 1.
- Robust to the non-linearity of timestamp-based progress via the rolling window (matches the
  "rate over the last minute" intuition).

### GUI wiring (Part B)
- `DashboardViewModel`: an `EtaEstimator` instance; subscribe to `Progress` updates (the VM already
  watches `Progress.Current`); on each change call `Report(Progress.Current.Fraction, clock)` and
  expose an observable `EtaText` (e.g. `"~2m14s left"`, `"estimating…"`, or empty when idle/done).
  `Reset()` at the start of each export/continue run; clear when `IsBusy` goes false.
- Time source is injectable (a `Func<DateTimeOffset>`), defaulting to wall clock, so the estimator
  is unit-testable. (Note: scripts/workflows ban `DateTimeOffset.Now`; the GUI runtime does not —
  the estimator reads the injected clock.)
- A `TextBlock` next to the dashboard `ProgressBar` bound to `EtaText`; visible only while busy.
- New localization strings: `EtaEstimating`, `EtaRemainingFormat` (`"~{0} left"`).

### Non-goals (Part B)
- **Messages/sec readout.** The progress signal is fraction-based, not count-based, and there's no
  total count, so a true msg/s rate would require threading a live message counter through the
  Core export → progress pipeline (a separate, more invasive change). Deferred; can be added later
  if desired. ETA shows **time remaining only** for now.
- Per-channel ETA breakdown during multi-channel export (the dashboard shows one aggregate bar;
  ETA is aggregate to match).

### Testing (Part B)
`EtaEstimator` unit tests with an injected clock: returns null before confidence; computes a correct
estimate for a steady rate; updates as rate changes; handles fraction stalls (rate 0 → null);
returns ~0/empty at completion; reset clears samples.

## Failure-mode check
1. **HTML splice markers shift due to minification differences** → both existing and new files are
   produced by the same minifier with the same options, so the `<div class="postamble">` and
   chatlog-close markers are stable; parsing is quote-tolerant. Merge writes to temp + atomic
   replace, so a failed splice cannot corrupt the original. *Mitigated.*
2. **CSV boundary duplicate / same-ms ambiguity** → boundary skip (`≤ cutoff`) removes the common
   dup; rare same-ms edge accepted (documented; CSV has no id). *Minor, documented.*
3. **Filename lacks `[id]`** (renamed/custom) and format has no in-file id (CSV) → refused with
   guidance rather than guessing the wrong channel. *Handled.*
4. **ETA wild early estimates** → "estimating…" gate until the window is confident; rolling window
   smooths spikes. *Mitigated.*
5. **HTML count regex misses a locale-grouped number** → match digits with optional grouping
   separators from the export `CultureInfo`; if not matched, leave the count unchanged (cosmetic)
   rather than corrupt the file. *Minor, contained.*

## Rollout
Rebuild the self-contained win-x64 GUI and redeploy over `outputs/DiscordChatExporter-user-copy/`
(as in v1). No schema changes; no migration.
