using DiscordChatExporter.Cli.Tests.Infra;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

// Real minifier behavior observed (WebMarkupMin.Core 2.21.0, default HtmlMinifier settings):
//   chatlog open:     <div class="chatlog">          — QUOTED (wmm:ignore-protected verbatim)
//   data-message-id:  data-message-id=1000           — UNQUOTED (Html5 mode, single-token value)
//   postamble open:   <div class=postamble>           — UNQUOTED (Html5 mode, single-token class)
// These assertions are quote-tolerant so they will survive if a future minifier version changes
// quoting behavior, but the Contain checks pin the forms that MUST stay stable for the merger.
public class HtmlSampleSpecs
{
    [Fact]
    public void The_sample_export_preserves_the_splice_anchors_and_message_ids()
    {
        var html = HtmlSample.Export([1000L, 2000L]);
        // chatlog open survives verbatim (wmm:ignore-protected), quotes intact:
        html.Should().Contain("<div class=\"chatlog\">");
        // postamble open present — actual observed form: <div class=postamble> (unquoted, Html5):
        html.Should().MatchRegex("<div class=\"?postamble\"?>");
        // both message ids present — actual observed form: data-message-id=1000 (unquoted, Html5):
        html.Should().MatchRegex("data-message-id=\"?1000\"?");
        html.Should().MatchRegex("data-message-id=\"?2000\"?");
        // count text present:
        html.Should().Contain("Exported 2 message(s)");
        // exactly one chatlog container and one postamble (sanity for the merger's gate later):
        System
            .Text.RegularExpressions.Regex.Matches(html, "<div class=\"chatlog\">")
            .Count.Should()
            .Be(1);
        System
            .Text.RegularExpressions.Regex.Matches(html, "<div class=\"?postamble\"?>")
            .Count.Should()
            .Be(1);
    }
}
