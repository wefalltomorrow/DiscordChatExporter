using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class FileNameChannelIdSpecs
{
    [Theory]
    [InlineData(@"C:\x\Guild - general [123456789012345678].csv", 123456789012345678UL)]
    [InlineData("Guild - general [42] (after 2026-01-01).html", 42UL)]
    [InlineData("My Server - parent - child [999].json", 999UL)]
    [InlineData("Guild [111] - channel [222].json", 222UL)]
    public void I_can_parse_the_channel_id_from_a_default_export_filename(
        string path,
        ulong expected
    )
    {
        FileNameChannelId
            .TryParse(path)
            .Should()
            .Be(new DiscordChatExporter.Core.Discord.Snowflake(expected));
    }

    [Theory]
    [InlineData(@"C:\x\my-renamed-export.csv")]
    [InlineData("chat.html")]
    [InlineData("notes [abc].txt")]
    [InlineData("Guild - general [999999999999999999999999999999].json")]
    [InlineData("Guild - general [१२३].json")]
    public void I_get_null_when_the_filename_has_no_channel_id(string path)
    {
        FileNameChannelId.TryParse(path).Should().BeNull();
    }
}
