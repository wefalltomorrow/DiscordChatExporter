using System;
using System.Collections.Generic;
using System.Linq;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using FluentAssertions;
using Gress;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportProgressSpecs
{
    private sealed class OddMessageFilter : MessageFilter
    {
        public override bool IsMatch(Message message) => message.Id.Value % 2 == 1;
    }

    private sealed class ProgressSpy : IProgress<ExportProgress>
    {
        public List<ExportProgress> Reports { get; } = [];

        public void Report(ExportProgress value) => Reports.Add(value);
    }

    private static User CreateUser() => new(new Snowflake(10), false, null, "alice", "alice", "");

    private static Message CreateMessage(ulong id) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            CreateUser(),
            DateTimeOffset.UnixEpoch.AddSeconds((long)id),
            null,
            null,
            false,
            id.ToString(),
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null
        );

    [Fact]
    public void Messages_read_counts_walked_messages_before_filtering()
    {
        var filter = new OddMessageFilter();
        var spy = new ProgressSpy();
        var messagesRead = 0L;
        var exported = 0;

        foreach (
            var message in new[]
            {
                CreateMessage(1),
                CreateMessage(2),
                CreateMessage(3),
                CreateMessage(4),
            }
        )
        {
            ChannelExporter.ReportWalkedMessage(
                message,
                Percentage.FromFraction(0.5),
                spy,
                ref messagesRead
            );

            if (filter.IsMatch(message))
                exported++;
        }

        exported.Should().Be(2);
        spy.Reports.Should().HaveCount(4);
        spy.Reports[^1].MessagesRead.Should().Be(4);
        spy.Reports[^1].CurrentTimestamp.Should().Be(CreateMessage(4).Timestamp);
    }

    [Fact]
    public void Export_progress_fraction_tracks_the_latest_reported_message_fraction()
    {
        var state = new ChannelExporter.ExportProgressState();
        var spy = new ProgressSpy();

        state.Report(Percentage.FromFraction(0.25));
        state.ReportWalkedMessage(CreateMessage(1), spy);

        state.Report(Percentage.FromFraction(0.75));
        state.ReportWalkedMessage(CreateMessage(2), spy);

        spy.Reports.Select(r => r.Fraction.Fraction).Should().Equal(0.25, 0.75);
        spy.Reports.Select(r => r.MessagesRead).Should().Equal(1, 2);
    }
}
