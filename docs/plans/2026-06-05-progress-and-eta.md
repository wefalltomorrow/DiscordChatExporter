# Honest Progress Bar + Time-Remaining — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Stop the progress bar racing-then-stalling and the ETA ballooning/jumping. **Phase 1** (drop-in `EtaEstimator` rewrite, no plumbing) de-balloons immediately and SHIPS ALONE. **Phase 2** (count-based progress via an approximate total) is the real fix and is GATED on a live-token verification (with a density-sampling fallback if it fails).

**Architecture:** See `docs/specs/2026-06-05-progress-and-eta-design.md`. Time-fraction is a biased work proxy; Phase 1 stabilizes the estimate over the same input; Phase 2 introduces a real (approximate) total message count so the bar/ETA are count-based and converge to exact.

**Tech Stack:** C# / .NET 10, System.Text.Json, Avalonia, Gress, xUnit + FluentAssertions. `TreatWarningsAsErrors=true`, CSharpier-on-build. Independent of the SQLite-resume and conversion features.

**Test:** `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~EtaEstimatorSpecs"` (Phase 1) and `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~ExportProgressSpecs|FullyQualifiedName~CountMessagesParsingSpecs|FullyQualifiedName~MessageCountEstimatorSpecs"` (Phase 2 token-free).

---

## PHASE 1 — `EtaEstimator` rewrite (ship this first; no pipeline changes)

### Task 1: Stable, non-ballooning estimator

**Files:**
- Modify: `DiscordChatExporter.Core/Exporting/EtaEstimator.cs`
- Modify: `DiscordChatExporter.Cli.Tests/Specs/EtaEstimatorSpecs.cs`

- [ ] **Step 1: Rewrite the tests** to the new contract (the old 60s-window two-sample test is replaced — the new estimator intentionally won't emit a confident number from two samples):

```csharp
// Helper: feed a steady stream and read the estimate.
[Fact]
public void Returns_estimating_until_confidence_gate_passes()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    eta.Report(0.0, t);
    eta.Report(0.01, t + TimeSpan.FromSeconds(1));
    eta.Estimate.Should().BeNull(); // < min elapsed/samples
}

[Fact]
public void Estimates_remaining_for_a_steady_stream()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    // 1% per second, steady, for 20s -> ~80s remaining at f=0.20.
    for (var i = 0; i <= 20; i++)
        eta.Report(i * 0.01, t + TimeSpan.FromSeconds(i));
    eta.Estimate.Should().NotBeNull();
    eta.Estimate!.Value.TotalSeconds.Should().BeInRange(50, 110); // smoothed, ballpark
}

[Fact]
public void A_long_pause_does_not_unboundedly_spike_the_estimate()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    for (var i = 0; i <= 20; i++)
        eta.Report(i * 0.01, t + TimeSpan.FromSeconds(i));
    var before = eta.Estimate!.Value;
    // 60s rate-limit pause: time jumps, fraction doesn't.
    eta.Report(0.20, t + TimeSpan.FromSeconds(80));
    var after = eta.Estimate!.Value;
    after.Should().BeLessThan(before * 4); // bounded, not a wild spike
}

[Fact]
public void Caps_absurd_estimates()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    // Crawl: 0.001% per second -> raw estimate is enormous; must be capped.
    for (var i = 0; i <= 30; i++)
        eta.Report(i * 0.00001, t + TimeSpan.FromSeconds(i));
    eta.Estimate!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(12));
    eta.IsCapped.Should().BeTrue();
}

[Fact]
public void Ignores_backward_fraction_steps_from_muxer_resets()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    for (var i = 0; i <= 20; i++)
        eta.Report(i * 0.01, t + TimeSpan.FromSeconds(i));
    var act = () => eta.Report(0.10, t + TimeSpan.FromSeconds(21)); // aggregate dropped
    act.Should().NotThrow();
    (eta.Estimate is null || eta.Estimate.Value >= TimeSpan.Zero).Should().BeTrue();
}

[Fact]
public void Completion_is_zero_and_reset_clears()
{
    var eta = new EtaEstimator();
    var t = DateTimeOffset.UnixEpoch;
    for (var i = 0; i <= 10; i++) eta.Report(i * 0.1, t + TimeSpan.FromSeconds(i));
    eta.Report(1.0, t + TimeSpan.FromSeconds(11));
    eta.Estimate.Should().Be(TimeSpan.Zero);
    eta.Reset();
    eta.Estimate.Should().BeNull();
}
```

- [ ] **Step 2: Run → fail.**

- [ ] **Step 3: Implement** the rewrite (keeps the `Report(double, DateTimeOffset)` / `Estimate` / `Reset` surface so the VM wiring is unchanged; adds `IsCapped`):

```csharp
using System;

namespace DiscordChatExporter.Core.Exporting;

// Stable time-remaining from a stream of (fraction, time) samples. Uses a time-aware EMA of the
// fraction-rate (so rate-limit pauses fold in at their true weight instead of dominating a short
// window), a confidence gate, asymmetric output smoothing (rises slowly, falls freely — feels
// monotonic without freezing a wrong number), and a hard cap. Pure + clock-injectable; single-thread.
public sealed class EtaEstimator(
    TimeSpan? tau = null,
    TimeSpan? minElapsed = null,
    int minSamples = 5,
    TimeSpan? cap = null
)
{
    private readonly double _tau = (tau ?? TimeSpan.FromSeconds(90)).TotalSeconds;
    private readonly double _minElapsed = (minElapsed ?? TimeSpan.FromSeconds(5)).TotalSeconds;
    private readonly double _cap = (cap ?? TimeSpan.FromHours(12)).TotalSeconds;
    private const double BetaUp = 0.15;
    private const double BetaDown = 0.5;

    private double? _rate; // EMA of dFraction/dt (per second)
    private double _shown; // smoothed displayed remaining (seconds)
    private bool _hasShown;
    private int _samples;
    private DateTimeOffset? _firstTime;
    private (double Fraction, DateTimeOffset Time)? _prev;
    private double _lastFraction;

    public void Report(double fraction, DateTimeOffset now)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _firstTime ??= now;
        _samples++;
        _lastFraction = fraction;

        if (_prev is { } p)
        {
            var dt = (now - p.Time).TotalSeconds;
            if (dt > 0)
            {
                var inst = Math.Max(0, (fraction - p.Fraction) / dt); // ignore backward steps
                var alpha = 1 - Math.Exp(-dt / _tau);
                _rate = _rate is null ? inst : _rate.Value + alpha * (inst - _rate.Value);

                var raw = RawRemaining(fraction, now);
                if (raw is { } r)
                {
                    if (!_hasShown)
                    {
                        _shown = r;
                        _hasShown = true;
                    }
                    else
                    {
                        var beta = r > _shown ? BetaUp : BetaDown;
                        _shown += beta * (r - _shown);
                    }
                }
            }
        }

        _prev = (fraction, now);
    }

    private double? RawRemaining(double fraction, DateTimeOffset now)
    {
        if (_firstTime is null)
            return null;
        var elapsed = (now - _firstTime.Value).TotalSeconds;
        if (elapsed < _minElapsed || _samples < minSamples)
            return null;
        if (fraction <= 0 || fraction >= 1)
            return null;
        if (_rate is not { } rate || rate <= 0)
            return null;
        return Math.Max(0, (1 - fraction) / rate);
    }

    public TimeSpan? Estimate
    {
        get
        {
            if (_lastFraction >= 1)
                return TimeSpan.Zero;
            if (!_hasShown)
                return null;
            return TimeSpan.FromSeconds(Math.Min(_shown, _cap));
        }
    }

    // True when the smoothed estimate exceeds the cap, so the UI can render ">12h" rather than a number.
    public bool IsCapped => _hasShown && _lastFraction < 1 && _shown > _cap;

    public void Reset()
    {
        _rate = null;
        _shown = 0;
        _hasShown = false;
        _samples = 0;
        _firstTime = null;
        _prev = null;
        _lastFraction = 0;
    }
}
```

- [ ] **Step 4: Run → pass.**
- [ ] **Step 5 (display polish, optional but recommended):** in `DashboardViewModel.UpdateEta`, when `_etaEstimator.IsCapped`, set `EtaText` to the localized "> 12 h" form instead of a precise number; otherwise format `Estimate` as today. Keep all existing `Report(...)`/`Reset()` call sites (they pass a clock + fraction unchanged). Build the GUI → 0/0.
- [ ] **Step 6: Commit** `"Progress/ETA Phase 1: stable, capped, non-ballooning EtaEstimator"`.

---

## PHASE 2 — count-based progress (gated on search verification; density fallback)

> **Before starting Phase 2, verify with a live user token** that a filter-only message search
> (`GET .../messages/search?channel_id=…&min_id=…&max_id=…&limit=1`, no `content`) returns
> `total_results` for the range. If it does → use Task 7's search path. If not → rely on Task 8's
> density sampling. Either way the bar/ETA degrade to Phase 1 when no total is available.

### Task 7: `CountMessagesAsync` (approximate total via search)

**Files:**
- Modify: `DiscordChatExporter.Core/Discord/DiscordClient.cs`
- Test: `DiscordChatExporter.Cli.Tests/Specs/CountMessagesParsingSpecs.cs` (parsing only — live HTTP can't be token-free tested; cover `total_results` extraction, 202→null, missing-field→null against captured JSON strings; gate live behavior behind the manual smoke test)

- [ ] Add a method that returns an approximate in-range count, or `null` on any failure (202, missing
  field, bot token, exception). Use the existing `TryGetJsonResponseAsync` + `UrlBuilder`/`SetQueryParameter`
  patterns (see `GetChannelThreadsAsync` ~455–508 for the established search-call shape). Build the route
  per channel kind (guild: `guilds/{channel.GuildId}/messages/search?channel_id={channel.Id}`; DM/group:
  `channels/{channel.Id}/messages/search`), add `min_id`/`max_id` from `after`/`before` (raw snowflake
  ids), `limit=1`. Read `total_results` as int. Return `null` (never throw) when the response is null/202/
  missing the field. Clamp/interpret at the call site (undercount possible). Skip entirely for bot tokens.
- [ ] Parsing tests + commit `"Progress/ETA Phase 2: CountMessagesAsync (approx total via search)"`.

### Task 8: Density-sampling estimator + size tiering

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/Progress/MessageCountEstimator.cs` (pure integration of
  density samples → total; token-free testable)
- Modify: `DiscordChatExporter.Core/Discord/DiscordClient.cs` (collect ~K probe pages via the
  `TryGetFirstMessageAsync`/`TryGetLastMessageAsync` pattern ~598–630, `after=FromDate(t_i)&limit=100`)

- [ ] Pure estimator: given `(timestamp, localDensity)` probe points over `[t0,t1]`, trapezoidally
  integrate → estimated total; unit-test forward (convex) and reverse (concave) synthetic curves converge
  within tolerance. Size tiering: expose a "should-estimate" check (skip when the first in-range page is
  the only page). Commit `"Progress/ETA Phase 2: density-sample count estimator + size tiering"`.

### Task 9: Thread `ExportProgress` through the export

**Files:**
- Create: `DiscordChatExporter.Core/Exporting/ExportProgress.cs`
- Modify: `DiscordChatExporter.Core/Exporting/ChannelExporter.cs` (signature + walked counter)
- Modify: `DiscordChatExporter.Cli/Commands/...ExportCommand` and `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` (callers of `ExportChannelAsync`)
- Test: `DiscordChatExporter.Cli.Tests/Specs/ExportProgressSpecs.cs`

- [ ] New record:

```csharp
using System;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

// Richer per-channel progress: the time-fraction (for the fallback bar), the monotonic count of
// messages the API has yielded (PRE-filter — the honest "work done"), and the timestamp currently
// being processed (for the "exported through <date>" display).
public readonly record struct ExportProgress(
    Percentage Fraction,
    long MessagesRead,
    DateTimeOffset? CurrentTimestamp
);
```

- [ ] Change `ChannelExporter.ExportChannelAsync` to take `IProgress<ExportProgress>?`. Internally keep an
  `IProgress<Percentage>` for `GetMessagesAsync`; in the `await foreach` (~84) increment a **walked**
  counter for EVERY yielded message (before the `MessageFilter.IsMatch` check at ~93 — NOT
  `MessagesExported`, which is post-filter), capture `message.Timestamp`, and on each underlying fraction
  report forward `new ExportProgress(fraction, walked, currentTimestamp)` to the outer progress. Test: a
  `MessageFilter` that drops half the messages → `MessagesRead` reflects the FULL count, not the exported
  half.
- [ ] Update the CLI + GUI callers to the new progress type (the GUI muxer work is Task 10). Build → 0/0.
- [ ] Commit `"Progress/ETA Phase 2: thread ExportProgress (pre-filter walked count + timestamp)"`.

### Task 10: Count-based bar + ETA + display in the GUI

**Files:**
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs`
- Modify: `DiscordChatExporter.Gui/Views/Components/DashboardView.axaml`
- Modify: `DiscordChatExporter.Gui/Localization/LocalizationManager(.English).cs`

- [ ] In the VM: sum `ExportProgress.MessagesRead` across the parallel channels into a monotonic
  `walked` (thread-safe); hold the per-run `estimatedTotal` (from Task 7 ?? Task 8 ?? none) and
  continuously correct it toward exact (`estimatedTotal = walked + correction × remainingModel`).
  - **Bar:** `Math.Clamp(walked / estimatedTotal, 0, 1)`, clamped to never decrease; **fallback** to the
    existing muxed time-fraction when `estimatedTotal` is null. For whole-server, also track
    `channelsDone / channelCount`.
  - **ETA:** feed the Phase-1 `EtaEstimator` with `walked / estimatedTotal` (so it's now count-based);
    keep its smoothing/cap/gate.
  - **Display:** add observable props for `MessagesReadText` ("12,480 messages"), `RateText` ("310/s",
    EMA of Δwalked/Δt), `ExportedThroughText` (current timestamp → "exported through Mar 2024") and/or
    `ChannelProgressText` ("Channel 7 of 41"). Replace the single ETA `TextBlock` (`DashboardView.axaml`
    ~107–118) with this status block. Add localization strings.
- [ ] Build → 0/0. Commit `"Progress/ETA Phase 2: count-based bar + ETA + honest status display"`.

### Task 11: Rate-limit pause indicator

**Files:**
- Modify: `DiscordChatExporter.Core/Discord/DiscordClient.cs` (surface the backoff `Task.Delay` ~86)
- Modify: `DiscordChatExporter.Gui/ViewModels/Components/DashboardViewModel.cs` + `DashboardView.axaml`

- [ ] Surface the rate-limit wait (delay duration) via a callback/event/`IProgress<RateLimitState>` on
  `DiscordClient` so the VM can show "⏸ Rate limited — resuming in Ns…" and flip the bar to
  indeterminate. **Softer fallback if the signal is too invasive:** in the VM, flip the bar to
  indeterminate whenever no progress has been reported for > ~3 s (a heuristic that needs no Core change).
  Pick the cleaner of the two during implementation.
- [ ] Build → 0/0. Commit `"Progress/ETA Phase 2: rate-limit pause indicator"`.

### Task 12: Whole-feature verification + deploy

- [ ] `dotnet build DiscordChatExporter.slnx` → 0/0; `dotnet test DiscordChatExporter.Cli.Tests --filter "FullyQualifiedName~EtaEstimatorSpecs|FullyQualifiedName~ExportProgressSpecs|FullyQualifiedName~CountMessagesParsingSpecs|FullyQualifiedName~MessageCountEstimatorSpecs"` → green; `dotnet test DiscordChatExporter.Gui.Tests` → no regression.
- [ ] Trimmed self-contained publish succeeds (only pre-existing Material.Avalonia trim warnings).
- [ ] **Manual smoke (needs a token):** run a large export — confirm the message counter climbs steadily, the bar no longer races-then-stalls, the ETA stays sane (no "90 h"), a rate-limit pause shows the pause state, and the search-count path actually returns a total (the Phase 2 verification gate). Redeploy over the user-copy, preserving `Settings.dat`.

---

## Notes for the implementer

- **Phase 1 is independent and the priority** — it needs no API/pipeline work and fixes the worst of the symptom. Ship it even if Phase 2 slips.
- **Phase 2 has real design latitude** (the `ExportProgress` threading vs the Gress muxer, the pause-signal mechanism). The plan recommends a concrete path for each but expect to read the surrounding wiring (`DashboardViewModel` progress/muxer setup, the CLI export command's progress arg) and adapt. This is the least turnkey of the three feature plans.
- **`CountMessagesAsync` live behavior can't be unit-tested token-free** — cover parsing against captured JSON, and rely on the manual smoke + the verification gate for the real endpoint.
- The whole feature must **never make the export worse**: every count path falls back to Phase 1's relabeled-fraction bar + smoothed ETA on any failure.
