# DiscordChatExporter fork — "what to build next" brainstorm

**Date:** 2026-06-03
**Method:** 3-Opus-agent team that talked to each other (Archivist / Analyst / Craftsman lenses),
two peer rounds (opening + cross-critique) then independent final shortlists.
**Status:** ideation only — nothing committed to a plan yet. Keep or delete this file freely.

Context the agents worked from: this fork already ships **Continue/incremental export (JSON/HTML/CSV)**,
**live ETA**, **whole-server export (Select-all → one file per channel)**, and **copy-user-messages**.
The brief was *new* features that build on those. Local-first desktop + CLI; no cloud backend.

---

## The headline: strong, independent convergence

All three agents — given different lenses and no shared starting list — ranked the **same foundation
first**, which is the strongest possible signal that it's the right next move:

> **Manifest/Catalog → Resilient batch (retry + resume) → things that read the manifest.**

The peer discussion also collapsed several separately-proposed ideas into single engines (see
"cross-lens insights" below). That merging is the real value of having them talk rather than just
listing ideas in parallel.

---

## Recommended set, in dependency/build order

Effort key: **S** = hours · **M** = a day or two · **L** = multi-day.

### Tier 0 — Foundation (build first; everything else reads these)

**1. Export Manifest / Catalog**  *(unanimous #1)*
- **What:** After any export (especially whole-server), emit a sidecar `manifest.json`: per-channel
  output path, format, message count, first/last message ID + timestamp, asset count, and sha256 of
  each output file. Versioned schema.
- **Why:** Single source of truth for "what do I have, how fresh, is it intact." Search, analytics,
  integrity-verify, resume, watch, and a library view all read it instead of re-parsing N files.
- **Effort:** M · **Hook:** new `Core/Exporting/Manifest/`; whole-server path (GUI `ExportAll` +
  CLI) writes it post-export. · **Risk:** schema churn → add a `version` field from day one.

**2. SQLite export format**
- **What:** New `ExportFormat` writing messages/authors/channels/reactions/attachments into a
  normalized single-file SQLite DB, with an FTS5 full-text index.
- **Why:** Portable single-file archive *and* the query substrate for cross-export search, analytics,
  and integrity counts. Highest leverage on the data side.
- **Effort:** M · **Hook:** new `SqliteMessageWriter : MessageWriter`, registered in the
  `ExportFormat` switch arms (the writer abstraction makes this clean). · **Risk:** schema design
  churn; adds `Microsoft.Data.Sqlite`.

### Tier 1 — Reliability (the biggest trust win for whole-server export)

**3. Resilient batch: per-channel retry + resume**  *(all three ranked high; two ideas merged into one engine)*
- **What:** In whole-server/multi-channel export, a failing channel (rate-limit, perms, crash) is
  recorded but does **not** abort the batch. "Retry failed (N)" re-runs only failures. Per-channel
  status persisted so a killed multi-hour run resumes where it stopped, not from scratch.
- **Why:** Today one bad channel at 180/200 sinks hours of work. Single biggest reliability/trust win.
- **Effort:** M · **Hook:** export loop in `DashboardViewModel` + CLI `ExportAll`; per-channel
  try/catch → failed-list; the manifest (#1) is the checkpoint store; existing continuation finishes
  the one partially-done channel. · **Risk:** defining "partial channel" — keep resume channel-granular.

### Tier 2 — High value, mostly standalone payoffs

**4. Stats / Insights report**  *(best value-per-effort on analytics)*
- **What:** Optional post-export `Insights.html`: activity timeline, top posters, busiest hours/days,
  emoji + reaction leaderboard, word frequency.
- **Why:** Turns a raw dump into a shareable "year in review" — high perceived value, low effort.
- **Effort:** M · **Hook:** RazorBlade template alongside the existing HTML writer (RazorBlade is
  already in the stack); one "Open Insights" button. · **Risk:** word-freq needs stopword/locale
  handling; cap/sample on very large channels (needs a buffered second pass — pipeline is streaming).

**5. "Library" home view**  *(hybrid: manifest + recent-jobs/one-click re-run)*
- **What:** Dashboard reads manifests from a watched folder and shows a live catalog — per channel:
  freshness, message count, format, integrity badge — each row with one-click **Continue / Re-run /
  Verify**. Becomes the app's home screen.
- **Why:** Most exports are repeats; collapses multi-field setup into one click and makes the archive
  legible at a glance. Turns the data layer into everyday UX.
- **Effort:** M · **Hook:** `DashboardView.axaml` ItemsControl bound to a manifest-reading service;
  reuses #3's per-channel status-row template; natural host for search (#8). · **Risk:** stale refs
  (deleted channels / missing files) need graceful badges, not crashes. · **Needs:** #1.

**6. Completion notifications + summary toast**  *(cheapest win — good first ship for momentum)*
- **What:** OS notification + in-app summary card on finish: X messages, Y assets, Z MB, duration,
  and failures (N channels).
- **Why:** Long exports run unattended; users want a clear "done" signal + health check.
- **Effort:** S · **Hook:** fire on export-complete in `DashboardViewModel`; thin cross-platform
  notification abstraction. · **Risk:** cross-platform notification APIs differ — keep it thin.
  · **Pairs with:** #3 (can then report per-channel failures).

### Tier 3 — Bigger bets (the "dream" features; need the foundation)

**7. Scheduled auto-continue ("watch" mode)**  *(the signature automation payoff)*
- **What:** Daemon/loop mode: every N minutes/hours, re-run continue-export on a manifest's channels,
  appending only new messages. GUI watch-dashboard shows each channel's last-run / next-run countdown
  / new-message count / outcome, with a per-cycle toast.
- **Why:** Hands-off live archival of active servers — directly amplifies the fork's continue-export
  signature. The GUI dashboard makes a headless daemon trustworthy.
- **Effort:** L · **Hook:** new CLI `WatchCommand` wrapping the Continuation module + a timer; GUI
  observable collection + timer. · **Risk:** long-lived token expiry; rate limits. · **Needs:** #1 +
  #3 + existing continuation.

**8. Full-text search across exports**  *(the #1 thing people want from an archive)*
- **What:** Point a folder of exports at an index → GUI query box returns matching messages with
  channel + timestamp + context. FTS5 over the SQLite format (#2).
- **Why:** "Where did we talk about X" across everything ever exported.
- **Effort:** L · **Hook:** new `Core/Search/` + a GUI tab (lives well inside the Library view #5).
  · **Freshness (key insight):** the continue-export merger already produces a *new-message delta* —
  feed that straight into the FTS index in the same pass, so the index is never stale (sha256-reindex
  is the fallback for exports done outside the continue path). · **Needs:** #2 + #1.

**9. Persisted request layer → recent-jobs / presets / batch jobs**  *(build the schema once, serves four features)*
- **What:** One persisted "export request" object with two entry points: GUI "Recent exports /
  one-click re-run" list, and a declarative CLI `--jobs jobs.yaml` for headless multi-target runs
  (each continue-if-exists). A saved preset can be promoted to a recurring watch job.
- **Why:** Power-users re-export the same 20 servers weekly; collapses 20 setups into one. Build the
  request-persistence layer once → it feeds recent-list, presets, batch, *and* the watch scheduler (#7).
- **Effort:** M · **Hook:** `PersistedRequest` model in app-data; GUI history list; CLI `RunJobs`
  looping existing Export commands. · **Risk:** stale refs; leans on #3 for partial-failure semantics.

**10. Integrity Verify (+ edit/delete forensic diff)**  *(cheap trust layer with a unique twist)*
- **What:** `verify` re-hashes output files + re-counts messages against the manifest; reports
  drift/corruption/missing assets. **Optionally** reports "N messages edited, M deleted since last
  run" by diffing re-exported messages against the stored copy.
- **Why:** Trust for long-term archives after disk moves/backups. The edit/delete diff is genuinely
  unique — it recovers what Discord hides — and is on-brand for the continuation signature.
- **Effort:** S–M (verify alone), M–L with revision-diff · **Hook:** new CLI `VerifyCommand` reusing
  the manifest + `JsonExportInspector` parse; revision data should live *in* the manifest/SQLite so
  it's queryable. · **Risk:** deletes are ambiguous (deleted vs out-of-range) — store as "missing
  since," not an asserted delete. · **Needs:** #1.

---

## Cross-lens insights (what the agents' discussion produced that no single lens would)

- **The manifest is the keystone.** Search, analytics, verify, resume, watch, and the library view
  all read it. This is why all three put it at #1 and why it's the right first build.
- **Resilient-batch's failed-list *is* the resume checkpoint.** "Per-channel retry" (UX) and
  "resume-after-crash" (archival) are one engine, not two features.
- **One persisted-request object** serves recent-jobs, presets, batch jobs, *and* the watch scheduler.
  Build it once (#9) and four features fall out.
- **Index freshness is free via the continuation delta** (#8): the continue merger already knows the
  new messages — pipe them to the FTS index in the same pass; no stale-index problem.
- **The edit/delete forensic diff gets a cheap home** inside verify/manifest (#10) rather than being a
  standalone feature — recovering edited/deleted history is a standout, on-brand capability.
- **One viewer, two entry points:** the Insights report renderer (#4) can also render a pre-flight
  preview sample (see disagreement below).
- **The "Library" home view (#5) is the convergence surface** — the one place all three lenses meet.
  It's the manifest-driven catalog of what-you-have (archival), the one-click Continue/Re-run list
  that *is* the job history (UX, subsuming recent-jobs/presets from #9's GUI half), and the search box
  that queries the SQLite/FTS indexes across every export (analytics). Search gets an anchored home
  here instead of a standalone tab. The team's CLI-vs-GUI split: **Insights** = a generated HTML file
  + "Open Insights" button (no new GUI surface); **SQLite** = silent substrate; **Search** = the one
  feature that genuinely needs real GUI, and the Library is that GUI.

---

## Judgment calls & disagreements

- **Pre-flight preview** — Analyst & Craftsman both liked a "Preview" button that fetches ~last 50
  messages with current filters applied + a tiny stat strip, *before* committing to a multi-hour
  export (avoids wasting a run on a wrong `from:`/date filter). Effort M–L (M if it reuses #4's
  renderer). **Archivist dissented**, preferring a cheaper **"dry-run count" mode** (just report how
  many messages the current filters would capture) for most of the value at S effort. → *Decide based
  on appetite: dry-run count = cheap insurance; full preview = nicer but bigger.*

## Deliberately deferred by all three (good ideas, not now)

- **Asset dedup / content-addressed store** — high effort and it breaks the single-file portability
  that HTML/Obsidian-style outputs rely on; disk is cheap, payoff niche. Revisit behind a "bundle"
  escape-hatch later.
- **Standalone social/reply graph** — "wow once," weak everyday utility, layout perf risk. Fold a
  small version into the Insights report (#4) instead.

---

## If you only do a few

- **Cheapest morale win to ship today:** #6 Completion notifications (S).
- **Highest-leverage foundation:** #1 Manifest (M) → #3 Resilient batch (M). This pair directly hardens
  the fork's flagship whole-server export and unlocks almost everything else.
- **Biggest new capability:** #2 SQLite (M) → #8 Full-text search (L).
- **The dream that amplifies your continue-export signature:** #7 Watch mode (L).
