using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportSummarizerSpecs
{
    [Fact]
    public void Summarize_totals_messages_assets_and_bytes_across_channels()
    {
        var stats = new[]
        {
            new ChannelExportStats(100, 5, 1000),
            new ChannelExportStats(50, 2, 500),
        };

        var summary = ExportSummarizer.Summarize(
            stats,
            failedChannels: 0,
            duration: TimeSpan.FromSeconds(30)
        );

        summary.SucceededChannels.Should().Be(2);
        summary.FailedChannels.Should().Be(0);
        summary.TotalMessages.Should().Be(150);
        summary.TotalAssets.Should().Be(7);
        summary.TotalBytes.Should().Be(1500);
        summary.Duration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Summarize_passes_through_failed_count_and_handles_empty_input()
    {
        var summary = ExportSummarizer.Summarize([], failedChannels: 3, duration: TimeSpan.Zero);

        summary.SucceededChannels.Should().Be(0);
        summary.FailedChannels.Should().Be(3);
        summary.TotalMessages.Should().Be(0);
        summary.TotalBytes.Should().Be(0);
    }
}
