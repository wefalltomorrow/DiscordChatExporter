# Library Home View (#5) — Design

**Status:** Draft for approval (2026-06-04). Feature #5 of the ROUND 4 batch; last item in the `{1,2,3,5,6}` goal set.

## Goal

A first-class **Library** home view that surfaces past exports (the #1 manifest catalog) and lets the user **search message content** inside SQLite (`.db`, #2) exports. This is the convergence surface that ties #1 + #2 together.

## Decided UX (from the user, 2026-06-04)

1. **Discovery = auto-track + scan.** The app records the output folder of every export as it runs (a new persisted list), **and** the user can point the Library at a root folder which is recursively scanned for `manifest.json` files. The union of both sources forms the catalog.
2. **Placement = home view in the main window.** Today `MainView` hosts only the `Dashboard`. We add a view switch so the main `ContentControl` shows either the Dashboard or the Library, with navigation between them.
3. **Scope = browse + search only.** Catalog rows are read-only listings. **No** Continue/Re-run/Verify actions in the Library (Continue/Re-run remain on the Dashboard). **No** integrity/SHA-256 check. Deep edited/deleted-message diffing stays out (that is feature #10).

## Architecture

Three Core units (all token-free testable — the load-bearing logic lives here, per the same discipline as #2) and a thin GUI layer.

### Core (new namespace `DiscordChatExporter.Core.Exporting.Library`)

- **`ExportCatalog` / `ExportCatalogBuilder`** — aggregates `ManifestEntry` rows. Given a set of directories, reads each `manifest.json` via the existing `ManifestReader.TryReadAsync` (tolerant — skips unreadable/missing), flattens all entries, and de-dupes by absolute file path (case-insensitive). Returns the catalog rows. Pure aggregation + a recursive `manifest.json` finder for the "scan a folder" path. Token-free unit tests build temp dirs with manifests and assert aggregation/dedup/scan.
- **`SqliteExportReader`** — the SQLite read/query layer (the piece the advisor flagged as the must-build, test-first Core unit). Opens a `.db` **read-only** (`Mode=ReadOnly`, `Pooling=False`), runs an FTS5 `MATCH`, returns hits. Token-free tested by writing a `.db` with `SqliteMessageWriter` (already available) then querying it.
  - API: `ValueTask<IReadOnlyList<SqliteSearchHit>> SearchAsync(string databaseFilePath, string query, int limit, CancellationToken)` for one db, plus `ValueTask<IReadOnlyList<SqliteSearchHit>> SearchAcrossAsync(IReadOnlyList<string> databaseFilePaths, string query, int limitPerDatabase, CancellationToken)` which runs `SearchAsync` over each path (skipping any that are missing/unreadable) and concatenates — this backs the **global** search the user chose.
  - `SqliteSearchHit(string DatabaseFilePath, string MessageId, string Timestamp, string AuthorName, string Snippet)` — carries its source db path so the GUI can label each hit by joining back to the catalog entry (guild/channel) for that file.
  - SQL: `SELECT m.id, m.timestamp, a.name, snippet(messages_fts, 0, '«', '»', '…', 16) FROM messages_fts JOIN messages m ON m.id = messages_fts.message_id LEFT JOIN authors a ON a.id = m.author_id WHERE messages_fts MATCH $q ORDER BY rank LIMIT $limit;`
  - **FTS5 query safety (critical):** raw user input passed to `MATCH` throws on FTS5 operator characters. We wrap the user's text as a single quoted FTS5 string — double every embedded `"`, then surround with `"` — so input is treated as a literal phrase/term, never as FTS syntax. (A blank/whitespace query returns no hits without touching the db.)

### GUI

- **Auto-track persistence.** Add `SettingsService.KnownExportDirs` (a `string[]`, persisted by Cogwheel like the other settings). When an export completes, `DashboardViewModel` records the export's output directory (de-duped, most-recent-first, capped at a sane N e.g. 50). This is additive to the existing per-channel manifest write.
- **View switch.** `MainViewModel` gains a `CurrentPage` (the `DashboardViewModel` or a new `LibraryViewModel`) that `MainView`'s `ContentControl` binds to (via `DataTemplates` mapping VM→View, the existing MVVM pattern). A toolbar button on the Dashboard switches to the Library; the Library has a "back to Dashboard" affordance.
- **`LibraryViewModel` + `LibraryView`.** On open, builds the catalog from `KnownExportDirs`; a "Scan folder…" button lets the user add a root to scan. Lists catalog rows (guild · channel · format · message count · exported-at · file path). A single search box searches **across all `.db` exports** in the catalog at once: it collects every catalog entry whose file is a SQLite export, calls `SqliteExportReader.SearchAcrossAsync` with their paths, and lists the hits each labeled by its source export (guild/channel, via joining `hit.DatabaseFilePath` back to the catalog entry's `File`) plus author · timestamp · snippet. If the catalog contains no `.db` exports, the search box is disabled with a hint ("Search requires a SQLite (.db) export").

## Data flow

Export completes → DashboardVM writes per-channel `manifest.json` (existing) **and** appends the output dir to `KnownExportDirs` (new). User opens Library → `ExportCatalogBuilder` reads manifests from `KnownExportDirs` (+ any scanned root) → rows shown. User selects a `.db` row, types a query → `SqliteExportReader.SearchAsync` → hits shown.

## Testing strategy

- **Core (token-free, test-first):** `SqliteExportReader` (write `.db` files via `SqliteMessageWriter`, assert single-db MATCH hits, snippet, ranking, the quoting/escaping of special-char queries, empty-query short-circuit, graceful handling of a non-`.db`/corrupt/missing path, AND `SearchAcrossAsync` aggregating over multiple dbs with one bad path skipped). `ExportCatalogBuilder` (temp dirs with manifests → aggregation, dedup, recursive scan, tolerance of missing/garbage manifests).
- **GUI:** keep light. The one potentially load-bearing binding question — does the `MainView` `ContentControl` correctly swap between Dashboard and Library view models — can be covered by one headless `Gui.Tests` fact if it proves non-trivial (mirroring how Round 3 verified programmatic tree selection). Otherwise rely on the Core tests + the user's GUI smoke.

## Explicitly deferred (not in #5)

- Continue/Re-run from the Library (stay on the Dashboard).
- Integrity / SHA-256 "still intact?" check.
- Edited/deleted-message diff vs Discord (feature #10).

## Resolved decisions

- **Search granularity (approved 2026-06-04): global** — one search box queries all `.db` exports in the catalog at once, results labeled by source export.
- Design approved by the user to proceed to the implementation plan.
