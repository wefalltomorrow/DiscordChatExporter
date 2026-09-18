using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class HtmlRenderingSafetySpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceHtmlSafe_" + Guid.NewGuid().ToString("N")
    );

    public HtmlRenderingSafetySpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static User CreateUser() => new(new Snowflake(10), false, null, "alice", "alice", "");

    private static Message CreateMessage(string content) =>
        new(
            new Snowflake(1000),
            MessageKind.Default,
            MessageFlags.None,
            CreateUser(),
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            content,
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

    private static Message CreateForwardedMessage(MessageSnapshot forwardedMessage) =>
        new(
            new Snowflake(1000),
            MessageKind.Default,
            MessageFlags.None,
            CreateUser(),
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            "forwarded",
            [],
            [],
            [],
            [],
            [],
            new MessageReference(
                MessageReferenceKind.Forward,
                new Snowflake(999),
                new Snowflake(2),
                new Snowflake(1)
            ),
            null,
            forwardedMessage,
            null
        );

    private ExportContext CreateContext(string outputPath, bool shouldFormatMarkdown) =>
        new(
            new DiscordClient("fake-token"),
            new ExportRequest(
                new Guild(new Snowflake(1), "Test Guild", ""),
                new Channel(
                    new Snowflake(2),
                    ChannelKind.GuildTextChat,
                    new Snowflake(1),
                    null,
                    "general",
                    null,
                    null,
                    null,
                    false,
                    null
                ),
                outputPath,
                null,
                ExportFormat.HtmlDark,
                null,
                null,
                PartitionLimit.Null,
                MessageFilter.Null,
                isReverseMessageOrder: false,
                shouldFormatMarkdown,
                shouldDownloadAssets: false,
                shouldReuseAssets: false,
                locale: "en-US",
                isUtcNormalizationEnabled: true
            )
        );

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("https://cdn.example/image.png", "https://cdn.example/image.png")]
    [InlineData("Attachments/image.png", "Attachments/image.png")]
    [InlineData("javascript:alert(1)", "#")]
    [InlineData("data:text/html,<script>alert(1)</script>", "#")]
    public void Html_asset_url_sanitizer_preserves_safe_urls_and_blocks_unsafe_schemes(
        string url,
        string expected
    )
    {
        HtmlMarkdownVisitor.SanitizeHtmlAssetUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData(" http://example.com ", "http://example.com")]
    [InlineData("Attachments/image.png", "#")]
    [InlineData("javascript:alert(1)", "#")]
    public void Html_link_url_sanitizer_only_allows_http_links(string url, string expected)
    {
        HtmlMarkdownVisitor.SanitizeHtmlLinkUrl(url).Should().Be(expected);
    }

    [Fact]
    public async Task Html_export_escapes_message_content_when_markdown_formatting_is_disabled()
    {
        var outputPath = Path.Combine(_dir, "chat.html");
        await using (var exporter = new MessageExporter(CreateContext(outputPath, false)))
        {
            await exporter.ExportMessageAsync(CreateMessage("""<img src=x onerror="alert(1)">"""));
        }

        var html = await File.ReadAllTextAsync(outputPath);

        html.Should().Contain("&lt;img src=x");
        html.Should().NotContain("""<img src=x onerror="alert(1)">""");
    }

    [Fact]
    public async Task Html_export_sanitizes_embed_author_and_title_urls()
    {
        var outputPath = Path.Combine(_dir, "chat.html");
        await using (var exporter = new MessageExporter(CreateContext(outputPath, true)))
        {
            await exporter.ExportMessageAsync(
                CreateMessage("embed") with
                {
                    Embeds =
                    [
                        new Embed(
                            "unsafe title",
                            EmbedKind.Rich,
                            "javascript:alert(1)",
                            null,
                            null,
                            new EmbedAuthor("unsafe author", "javascript:alert(2)", null, null),
                            "description",
                            [],
                            null,
                            [],
                            null,
                            null
                        ),
                    ],
                }
            );
        }

        var html = await File.ReadAllTextAsync(outputPath);

        html.Should().Contain("chatlog__embed-author-link href=#");
        html.Should().Contain("chatlog__embed-title-link href=#");
        html.Should().NotContain("javascript:alert");
    }

    [Fact]
    public async Task Html_export_sanitizes_attachment_urls()
    {
        var outputPath = Path.Combine(_dir, "chat.html");
        await using (var exporter = new MessageExporter(CreateContext(outputPath, true)))
        {
            await exporter.ExportMessageAsync(
                CreateMessage("attachment") with
                {
                    Attachments =
                    [
                        new Attachment(
                            new Snowflake(50),
                            "javascript:alert(1)",
                            "proof.png",
                            null,
                            1,
                            1,
                            FileSize.FromBytes(1)
                        ),
                    ],
                }
            );
        }

        var html = await File.ReadAllTextAsync(outputPath);

        html.Should().Contain("href=#");
        html.Should().Contain("src=#");
        html.Should().NotContain("javascript:alert");
    }

    [Fact]
    public async Task Html_export_renders_forwarded_embeds_and_hides_forwarded_spoiler_attachments()
    {
        var outputPath = Path.Combine(_dir, "forwarded.html");
        await using (var exporter = new MessageExporter(CreateContext(outputPath, true)))
        {
            await exporter.ExportMessageAsync(
                CreateForwardedMessage(
                    new MessageSnapshot(
                        DateTimeOffset.UnixEpoch,
                        null,
                        "forwarded body",
                        [
                            new Attachment(
                                new Snowflake(51),
                                "https://cdn.example/SPOILER_secret.png",
                                "SPOILER_secret.png",
                                null,
                                1,
                                1,
                                FileSize.FromBytes(1)
                            ),
                        ],
                        [
                            new Embed(
                                "Forwarded embed title",
                                EmbedKind.Rich,
                                "https://example.com/embed",
                                null,
                                null,
                                null,
                                "Forwarded embed description",
                                [],
                                null,
                                [],
                                null,
                                null
                            ),
                        ],
                        []
                    )
                )
            );
        }

        var html = await File.ReadAllTextAsync(outputPath);

        html.Should().Contain("Forwarded embed title");
        html.Should().Contain("Forwarded embed description");
        html.Should().Contain("chatlog__attachment--hidden");
        html.Should().Contain("chatlog__attachment-spoiler-caption");
    }

    [Fact]
    public async Task Masked_markdown_links_do_not_render_active_javascript_urls()
    {
        var outputPath = Path.Combine(_dir, "chat.html");
        var context = CreateContext(outputPath, true);

        var html = await HtmlMarkdownVisitor.FormatAsync(
            context,
            "[click](javascript:alert(1))",
            false
        );

        html.Should().Contain("""href="#">click</a>""");
        html.Should().NotContain("javascript:alert");
    }
}
