using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class EtaEstimatorSpecs
{
    [Fact]
    public void Returns_estimating_until_confidence_gate_passes()
    {
        var eta = new EtaEstimator();
        var t = DateTimeOffset.UnixEpoch;

        eta.Report(0.0, t);
        eta.Report(0.01, t + TimeSpan.FromSeconds(1));
        eta.Estimate.Should().BeNull();
    }

    [Fact]
    public void Estimates_remaining_for_a_steady_stream()
    {
        var eta = new EtaEstimator();
        var t = DateTimeOffset.UnixEpoch;

        for (var i = 0; i <= 20; i++)
            eta.Report(i * 0.01, t + TimeSpan.FromSeconds(i));

        eta.Estimate.Should().NotBeNull();
        eta.Estimate!.Value.TotalSeconds.Should().BeInRange(50, 110);
    }

    [Fact]
    public void A_long_pause_does_not_unboundedly_spike_the_estimate()
    {
        var eta = new EtaEstimator();
        var t = DateTimeOffset.UnixEpoch;

        for (var i = 0; i <= 20; i++)
            eta.Report(i * 0.01, t + TimeSpan.FromSeconds(i));

        var before = eta.Estimate!.Value;
        eta.Report(0.20, t + TimeSpan.FromSeconds(80));
        var after = eta.Estimate!.Value;

        after.Should().BeLessThan(before * 4);
    }

    [Fact]
    public void Caps_absurd_estimates()
    {
        var eta = new EtaEstimator();
        var t = DateTimeOffset.UnixEpoch;

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

        var act = () => eta.Report(0.10, t + TimeSpan.FromSeconds(21));

        act.Should().NotThrow();
        (eta.Estimate is null || eta.Estimate.Value >= TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void Completion_is_zero_and_reset_clears()
    {
        var eta = new EtaEstimator();
        var t = DateTimeOffset.UnixEpoch;

        for (var i = 0; i <= 10; i++)
            eta.Report(i * 0.1, t + TimeSpan.FromSeconds(i));

        eta.Report(1.0, t + TimeSpan.FromSeconds(11));
        eta.Estimate.Should().Be(TimeSpan.Zero);
        eta.Reset();
        eta.Estimate.Should().BeNull();
    }
}
