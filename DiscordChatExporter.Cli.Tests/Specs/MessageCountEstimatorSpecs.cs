using System;
using System.Linq;
using DiscordChatExporter.Core.Exporting.Progress;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class MessageCountEstimatorSpecs
{
    [Fact]
    public void Density_estimate_integrates_increasing_convex_samples()
    {
        var start = DateTimeOffset.UnixEpoch;
        var end = start + TimeSpan.FromSeconds(100);
        var samples = Enumerable
            .Range(0, 11)
            .Select(i =>
            {
                var fraction = i / 10d;
                return new MessageDensitySample(
                    start + TimeSpan.FromSeconds(100 * fraction),
                    1 + 4 * fraction * fraction
                );
            })
            .ToArray();

        var estimate = MessageCountEstimator.EstimateTotal(start, end, samples);

        estimate.Should().NotBeNull();
        estimate!.Value.Should().BeInRange(225, 245);
    }

    [Fact]
    public void Density_estimate_integrates_decreasing_reverse_samples()
    {
        var start = DateTimeOffset.UnixEpoch;
        var end = start + TimeSpan.FromSeconds(100);
        var samples = Enumerable
            .Range(0, 11)
            .Select(i =>
            {
                var fraction = i / 10d;
                return new MessageDensitySample(
                    start + TimeSpan.FromSeconds(100 * fraction),
                    5 - 4 * fraction * fraction
                );
            })
            .ToArray();

        var estimate = MessageCountEstimator.EstimateTotal(start, end, samples);

        estimate.Should().NotBeNull();
        estimate!.Value.Should().BeInRange(355, 380);
    }

    [Fact]
    public void Density_estimate_extends_nearest_sample_to_boundaries()
    {
        var start = DateTimeOffset.UnixEpoch;
        var end = start + TimeSpan.FromSeconds(100);
        var samples = new[]
        {
            new MessageDensitySample(start + TimeSpan.FromSeconds(25), 2),
            new MessageDensitySample(start + TimeSpan.FromSeconds(75), 2),
        };

        var estimate = MessageCountEstimator.EstimateTotal(start, end, samples);

        estimate.Should().Be(200);
    }

    [Theory]
    [InlineData(99, true, false)]
    [InlineData(100, false, false)]
    [InlineData(100, true, true)]
    public void Density_size_tiering_skips_until_the_first_page_is_full_and_more_exists(
        int firstPageCount,
        bool hasMoreMessages,
        bool expected
    )
    {
        MessageCountEstimator.ShouldEstimate(firstPageCount, hasMoreMessages).Should().Be(expected);
    }
}
