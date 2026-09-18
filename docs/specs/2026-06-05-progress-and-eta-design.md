# Honest Progress Bar + Time-Remaining — Design

**Status:** Approved (2026-06-05). Feature ③ of the batch (① SQLite resume, ② conversion). Independent
of ① and ②; touches the Discord API layer + the export progress pipeline + the GUI. Distilled from a
4-agent brainstorm (UX, estimation algorithm, signal source, total-count feasibility).

## Goal

Make the export **progress bar** and **time-remaining** honest and stable, instead of racing to ~60%
then crawling at "99%" while the ETA balloons to absurd values ("90 h") and jumps around.

## Problem (precise root cause)

The only progress signal today is **timestamp coverage**, reported inside the pagination loop:
`progress = (currentMsg.Timestamp − firstMsg.Timestamp) / (lastMsg.Timestamp − firstMsg.Timestamp)`
(`DiscordClient.cs` ~line 693 forward `GetMessagesAsync`, ~line 761 reverse `GetMessagesInReverseAsync`).

1. **Biased proxy:** time ≠ work. Message density accelerates toward the present, so a forward export
   covers most of the *calendar* range while doing little *work*, then grinds through the dense recent
   tail — the bar slows and the ETA blows up. **This is a bias, not noise; smoothing cannot remove it.**
   (It *inverts* for reverse-order exports — density decelerates — so any fix must be direction-agnostic,
   never hardcode "accelerating".)
2. **Volatile estimator:** `EtaEstimator.cs` differentiates the fraction over a **60 s window**
   (`rate = Δf/Δt`, `remaining = (1−f)/rate`). A rate-limit backoff (`DiscordClient.cs` ~76–87, up to
   60 s `Task.Delay`) landing in the window crashes the rate → ETA spikes.
3. **Non-monotonic aggregate:** whole-server exports average per-channel fractions via an
   `AutoResetProgressMuxer` (`DashboardViewModel.cs` ~70) that resets as channels complete, so the
   aggregate fraction can step *down* → jumpy.

**Key insight (load-bearing):** "show msgs/sec instead of the fraction" does **not** fix the balloon —
a count-based *local* projection equals the fraction-based one algebraically over the same window. The
balloon is bias in the work proxy; only a **non-local total estimate** removes it. The monotonic
message **count**'s real value is (a) an honest, never-backtracking display number and (b) a monotonic
multi-channel aggregate that sidesteps the muxer — not "smoothness".

## Feasibility of a total count (verified by research)

- **No exact total** for normal channels/DMs — `message_count`/`total_message_sent` are **thread-only**
  (`Channel` docs). 
- **Approximate total IS available** via the **message search** endpoint's `total_results`
  (`GET /guilds/{guild}/messages/search?channel_id=…` for guild channels;
  `GET /channels/{id}/messages/search` for DMs), scoped to the export range with `min_id`/`max_id`,
  `limit=1`. **Works with user tokens** (this app's mode); the app already calls
  `channels/{id}/threads/search` (`DiscordClient.cs` ~455–508) through the same GET/resilience infra, so
  it is low-risk. ~**1 extra request** per channel.
- **Caveats (why it's "approximate"):** search omits **system messages** (joins/boosts/pins) that the
  exporter emits → tends to **undercount** (clamp bar to ≤ 1.0); `total_results` "may not be accurate"
  while the channel is actively changing; a not-yet-indexed channel returns **202** (treat as "no
  estimate"). Bot tokens: the endpoint is preview-only as of 2026-01 and needs `MESSAGE_CONTENT` — so
  the count is **user-token only**; bots fall back to Phase 1.
- **⚠️ Verify before building Phase 2:** that a *filter-only* search (no `content`, just
  `channel_id`/`min_id`/`max_id`) is accepted and returns `total_results` for the whole range. This is
  the load-bearing premise; it could not be tested without a live token. If it fails, Phase 2's total
  falls back to **density sampling** (below), which needs no search endpoint.

## Architecture — two phases

Phase 1 is a self-contained de-balloon with **no pipeline changes** and ships independently. Phase 2 is
the real fix (count-based) and is gated on the search verification (with a density fallback).

### Phase 1 — rewrite `EtaEstimator` (no new plumbing)

Replace the 60 s windowed differentiator with a stable estimator over the **same time-fraction input**:

- **Long-horizon rate:** a time-aware EMA of `Δf/Δt` (`alpha = 1 − exp(−dt/TAU)`, `TAU ≈ 90 s`) or a
  whole-run cumulative rate (`f / elapsed`). Folds rate-limit pauses in at their true weight instead of
  letting a 60 s window be dominated by one backoff.
- **Confidence gate:** return `null` ("estimating…") until elapsed ≥ ~5 s, ≥ ~5 samples, `f ∈ (0,1)`,
  rate > 0.
- **Asymmetric display smoothing** (in the VM or estimator): the shown ETA rises slowly
  (`beta_up ≈ 0.15`) and falls freely (`beta_down ≈ 0.5`). Do **NOT** hard-clamp "never increase" — that
  locks in optimism. This gives the *feel* of monotonicity without freezing a wrong number.
- **Cap + hysteresis:** clamp the displayed value to a ceiling (show `">12 h"`, never `"90 h"`); only
  repaint when the formatted string changes or the value moves > ~5 %.

This still inherits the density bias (ETA reads low through the middle, grows toward the end) — but it
stops the wild jumping and the absurd numbers. It's the immediate relief.

### Phase 2 — count-based progress (the real fix)

**(a) `DiscordClient.CountMessagesAsync(Channel channel, Snowflake? after, Snowflake? before, ct) → long?`**
- Guild channel → `GET guilds/{channel.GuildId}/messages/search?channel_id={id}&min_id={after}&max_id={before}&limit=1`;
  DM/group → `GET channels/{id}/messages/search?min_id=…&max_id=…&limit=1`. Build `min_id`/`max_id` from
  the export's `after`/`before` (or the channel's first/last id) via `Snowflake.FromDate`/the raw ids.
- Use the existing `TryGetJsonResponseAsync`; read `root.GetProperty("total_results").GetInt32()`.
- Return `null` on 202 / null / missing field / bot token / any failure → caller falls back to Phase 1
  behavior. Never throws into the export.

**(b) Density-sampling fallback** (token-agnostic; used when `CountMessagesAsync` returns null, or always
as a cross-check): pick K ≈ 8–12 probe timestamps across `[t0,t1]` (weighted toward the recent tail
where density changes fastest), fetch one `after=FromDate(t_i)&limit=100` page each, compute local
density `d_i = 100 / spanOfThosePages`, trapezoidally integrate `d(t)` over `[t0,t1]` → estimated total.
~K extra calls (negligible vs thousands for a real export). Reuse the `TryGetFirstMessageAsync`/
`TryGetLastMessageAsync` probe pattern (`DiscordClient.cs` ~598–630).

**(c) Size tiering:** if the first page exhausts the channel-in-range (< 100 messages, or no more after
page 1), **skip estimation entirely** — show a near-instant determinate bar from the page count. Don't
pay 10 probes for a 2-page export. Below the threshold, the old time-based signal is fine.

**(d) Thread a richer progress payload.** Introduce
`ExportProgress(Percentage Fraction, long MessagesRead, DateTimeOffset? CurrentTimestamp)` and change
`ChannelExporter.ExportChannelAsync` to take `IProgress<ExportProgress>?`. `ChannelExporter` keeps the
existing `IProgress<Percentage>` internally for `GetMessagesAsync`, and in its `await foreach`
(`ChannelExporter.cs` ~84) increments a **messages-walked** counter (PRE-filter — count every message
the API yields, NOT `MessagesExported` which is post-`MessageFilter`), tracks `message.Timestamp`, and
forwards an `ExportProgress` to the outer progress on each underlying fraction report.

**(e) Monotonic aggregate + count-based bar/ETA in the VM.** The VM sums `MessagesRead` across the
parallel channels (thread-safe) → a monotonic `walked`. With an `estimatedTotal` (from a or b),
continuously correct it so it converges to exact:
`estimatedTotal = walked + correction × remainingModel`, where `correction = walked / modelForWalkedRange`.
- **Bar** = `walked / estimatedTotal`, **clamped monotonic (never decreases)** and clamped ≤ 1.0
  (search undercount). When no estimate exists (tiny channel / search+density both unavailable), fall
  back to the existing muxed time-fraction bar.
- **ETA** = `(estimatedTotal − walked) / smoothedMsgsPerSec`, smoothedMsgsPerSec = time-aware EMA of
  `Δwalked/Δt` (Phase 1's machinery, now fed by count). Asymmetric smoothing + cap + gate as Phase 1.
- For whole-server, the summed `walked` (and `channelsDone / channelCount`) replaces the auto-reset
  muxer as the bar driver — sidesteps the non-monotonic jitter entirely.

**(f) GUI display** (`DashboardView.axaml` ~107–118, `DashboardViewModel`): promote the honest signals.
- Single-channel: progress bar (count-based when available, else relabeled calendar bar) + a live
  **"N messages · R/s"** line + **"exported through <Mon YYYY>"** (the current message timestamp climbing
  toward now — turns the slow bar into informative calendar position).
- Whole-server: bar from `channelsDone/total` or summed count; **"Channel 7 of 41 · N messages · R/s"**.
- **Rate-limit pause indicator:** when a backoff is in progress, switch the bar to
  indeterminate/pulsing and show "⏸ Rate limited — resuming in Ns…" so a freeze never reads as a crash.
  Requires surfacing the `Task.Delay` (`DiscordClient.cs` ~86) via a callback/event carrying the delay
  (e.g. an `IProgress<RateLimitState>` or event on `DiscordClient`); if that's too invasive, a softer
  version is to flip to indeterminate whenever no progress has been reported for > ~3 s.
- Replace the single `EtaText` block; drop the hard "~90h" ETA in favor of rate + calendar/channel
  position, keeping a **smoothed, capped** ETA as a secondary "~" line.

## Data flow (Phase 2, single channel)

```
export start → size tiering → (CountMessagesAsync ?? densitySample ?? none) = estimatedTotal?
pagination → ChannelExporter counts walked (pre-filter) + currentTimestamp → ExportProgress
VM: walked (Σ across channels), correct estimatedTotal toward exact
    bar = clamp01(monotonic(walked/estimatedTotal))   [fallback: muxed fraction]
    eta = (estimatedTotal − walked) / EMA(msgs/sec), asymmetric-smoothed, capped
    display: "N messages · R/s", "exported through <date>" / "channel x/N", pause state
```

## Error handling / degradation

- Search 202 / unavailable / bot token / any failure → `CountMessagesAsync` returns null → density
  fallback → if that also fails/skipped → Phase 1 behavior (relabeled time bar + smoothed ETA). The
  feature **never** degrades worse than Phase 1, and never throws into the export.
- All probe/search calls scoped to the export's `[after, before]`; clamp bar to [0,1].

## Testing strategy

- **Phase 1 (`EtaEstimatorSpecs` rewritten):** steady synthetic stream → ETA within tolerance and
  non-increasing beyond smoothing; injected long pause → bounded spike (not unbounded); forward (convex)
  & reverse (concave) `(f, M)` streams both converge (direction-agnostic); confidence gate → "estimating…"
  early; completion → zero/hidden; `Reset()` clears. (The current two-sample "45 s" test changes —
  the new estimator intentionally won't emit a confident number from two samples.)
- **Phase 2 token-free:**
  - Density integration: feed a synthetic density curve → estimated total within tolerance; reverse case.
  - Walk-time correction: simulate `(walked, modelRemaining)` → estimate converges to the true total as
    `f→1`; bar monotonic; clamped ≤ 1 on undercount.
  - `ExportProgress` threading: a unit test that messages-walked counts pre-filter (a `MessageFilter`
    dropping half → walked is full count, exported is half).
  - `CountMessagesAsync`: hard to unit-test without a live token/HTTP mock; cover the response *parsing*
    (total_results extraction, 202→null, missing-field→null) against captured JSON fixtures, and gate the
    live behavior behind the manual smoke test. Document the verify-with-live-token step.

## Scope / non-goals

- Approximate total only; clamp + fall back, never block the export on it.
- No new dependency; reuse the existing HTTP/resilience/search infra.
- Phase 1 ships alone and is the priority; Phase 2 is gated on the search verification (density fallback
  if it fails).
- Per-message live count is now cheap to thread (we already iterate pages) — this **supersedes** the
  earlier ETA design's "msgs/sec is a Non-goal" note in
  `docs/specs/2026-06-03-continue-multiformat-and-eta-design.md` (Part B); flag that supersede.

## File map

- `DiscordChatExporter.Core/Exporting/EtaEstimator.cs` — Phase 1 rewrite.
- `DiscordChatExporter.Cli.Tests/Specs/EtaEstimatorSpecs.cs` — Phase 1 tests change.
- `DiscordChatExporter.Core/Discord/DiscordClient.cs` — add `CountMessagesAsync` + density probe; fraction
  report ~693/761; rate-limit `Task.Delay` ~86 (pause signal); existing search ~455–508; HTTP helpers
  ~136–187; first/last probes ~598–630.
- `DiscordChatExporter.Core/Discord/Snowflake.cs` — `FromDate`/`ToDate` (~9–23).
- `DiscordChatExporter.Core/Discord/Data/Channel.cs` — ids (~21,40,57–59).
- `DiscordChatExporter.Core/Exporting/ExportProgress.cs` — new payload record.
- `DiscordChatExporter.Core/Exporting/ChannelExporter.cs` — emit `ExportProgress`; walked counter at the
  foreach (~84); `MessagesExported` is post-filter (~93–94 — do NOT use as the walked count).
- `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` — muxer (~70), per-channel input,
  `_etaEstimator` (~49), `UpdateEta()`, count aggregation, new display props.
- `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml` — progress block (~107–118).
- `DiscordChatExporter.Gui/Localization/LocalizationManager(.English).cs` — new strings (~11–12 today).
- CLI: `DiscordChatExporter.Cli` export command also passes an `IProgress` — update its progress type too
  (search for the `IProgress<Percentage>`/progress wiring in the CLI export command).
