using System;

namespace DiscordChatExporter.Core.Exporting;

// Stable time-remaining from a stream of (fraction, time) samples. Uses a time-aware EMA of the
// fraction-rate (so rate-limit pauses fold in at their true weight instead of dominating a short
// window), a confidence gate, asymmetric output smoothing, and a hard cap.
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

    private double? _rate;
    private double _shown;
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
                var inst = Math.Max(0, (fraction - p.Fraction) / dt);
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
