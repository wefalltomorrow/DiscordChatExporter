using DiscordChatExporter.Core.Markdown.Parsing;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class MarkdownParserLinkSpecs
{
    [Fact]
    public void I_can_extract_link_urls_without_full_markdown_parsing()
    {
        // Arrange
        const string markdown =
            "Plain https://example.com/a, hidden <https://example.com/b>, masked [docs](https://example.com/c).";

        // Act
        var links = MarkdownParser.ExtractLinkUrls(markdown);

        // Assert
        links
            .Should()
            .Equal("https://example.com/a", "https://example.com/b", "https://example.com/c");
    }

    [Fact]
    public void Link_url_extraction_ignores_code_blocks()
    {
        // Arrange
        const string markdown =
            "Ignore `https://example.com/code` but keep [docs](https://example.com/docs).";

        // Act
        var links = MarkdownParser.ExtractLinkUrls(markdown);

        // Assert
        links.Should().Equal("https://example.com/docs");
    }
}
