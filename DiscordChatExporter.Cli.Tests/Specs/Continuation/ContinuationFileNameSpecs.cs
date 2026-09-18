using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class ContinuationFileNameSpecs
{
    [Theory]
    [InlineData("Guild - channel [123] (before 2026-01-01).csv")]
    [InlineData("Guild - channel [123] (2025-01-01 to 2026-01-01).html")]
    [InlineData("Guild - channel [123] (2026-01-01).csv")]
    [InlineData("Guild - channel [123] (before 2026-01-01) [part 2].csv")]
    public void It_detects_before_bounds_after_the_channel_id(string filePath)
    {
        ContinuationFileName.HasBeforeBoundHint(filePath).Should().BeTrue();
    }

    [Theory]
    [InlineData("Guild (before 2026-01-01) - channel [123].csv")]
    [InlineData("Guild - before-channel [123].csv")]
    [InlineData("Guild - channel [123] (after 2026-01-01).csv")]
    [InlineData("Guild - channel [123] (before lunch).csv")]
    [InlineData("Guild - channel [123] (topic before 2026-01-01).csv")]
    [InlineData("Guild - channel [123] (before ).csv")]
    [InlineData("Guild - channel [123] (2026-01-01 to ).csv")]
    public void It_ignores_non_bound_filename_text(string filePath)
    {
        ContinuationFileName.HasBeforeBoundHint(filePath).Should().BeFalse();
    }
}
