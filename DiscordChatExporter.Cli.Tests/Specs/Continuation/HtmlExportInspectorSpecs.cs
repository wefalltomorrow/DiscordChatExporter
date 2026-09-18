using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportInspectorSpecs
{
    private static async Task<string> WriteAsync(string html, string name = "Guild - general")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name} [222] - {Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    [Fact]
    public async Task I_can_read_the_exact_cutoff_count_and_channel_from_an_html_export()
    {
        var path = await WriteAsync(HtmlSample.Export([1000L, 2000L], [3000L]));
        try
        {
            var info = await HtmlExportInspector.InspectAsync(path);
            info.ChannelId.Value.Should().Be(222UL);
            info.Cutoff.Value.Should().Be(3000UL);
            info.CutoffIsExact.Should().BeTrue();
            info.ExistingCount.Should().Be(3);
            info.IsChronological.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_ignores_a_forged_message_id_in_message_body_text()
    {
        var path = await WriteAsync(
            HtmlSample.ExportDetailed(
                [(100L, "data-message-id=999999", false)],
                [(200L, "real last", false)]
            )
        );
        try
        {
            var cutoff = await HtmlExportInspector.InspectAsync(path);

            cutoff.Cutoff.Value.Should().Be(200UL);
            cutoff.ExistingCount.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_ignores_a_forged_message_id_in_message_body_link_attribute()
    {
        var path = await WriteAsync(
            HtmlSample.ExportDetailed(
                [
                    (
                        100L,
                        "<a href=\"https://example.test/?class=chatlog__message-container&data-message-id=999999\">link</a>",
                        false
                    ),
                ],
                [(200L, "real last", false)]
            )
        );
        try
        {
            var cutoff = await HtmlExportInspector.InspectAsync(path);

            cutoff.Cutoff.Value.Should().Be(200UL);
            cutoff.ExistingCount.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_ignores_a_forged_message_id_in_message_body_div_attribute_value()
    {
        var path = await WriteAsync(
            HtmlSample.ExportDetailed(
                [
                    (
                        100L,
                        "<div class=chatlog__sticker title=\"x class=chatlog__message-container data-message-id=999999\"></div>",
                        false
                    ),
                ],
                [(200L, "real last", false)]
            )
        );
        try
        {
            var cutoff = await HtmlExportInspector.InspectAsync(path);

            cutoff.Cutoff.Value.Should().Be(200UL);
            cutoff.ExistingCount.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_ignores_a_nested_message_container_in_message_body()
    {
        var path = await WriteAsync(
            HtmlSample.ExportDetailed(
                [
                    (
                        100L,
                        "<div class=chatlog__sticker><div class=chatlog__message-container data-message-id=999999></div></div>",
                        false
                    ),
                ],
                [(200L, "real last", false)]
            )
        );
        try
        {
            var cutoff = await HtmlExportInspector.InspectAsync(path);

            cutoff.Cutoff.Value.Should().Be(200UL);
            cutoff.ExistingCount.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_does_not_crash_on_an_oversized_message_id_in_body_text()
    {
        var path = await WriteAsync(
            HtmlSample.ExportDetailed(
                [(100L, "data-message-id=99999999999999999999999", false)],
                [(200L, "real last", false)]
            )
        );
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_does_not_parse_a_non_decimal_message_id_as_a_cutoff()
    {
        var html = HtmlSample
            .Export([100L, 200L])
            .Replace("data-message-id=200", "data-message-id=\"2099-01-01\"");
        var path = await WriteAsync(html);
        try
        {
            var cutoff = await HtmlExportInspector.InspectAsync(path);

            cutoff.Cutoff.Value.Should().Be(100UL);
            cutoff.ExistingCount.Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_an_html_export_with_no_messages()
    {
        var path = await WriteAsync(HtmlSample.Export());
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_an_html_export_with_mixed_message_order()
    {
        var path = await WriteAsync(HtmlSample.Export([1000L, 3000L], [2000L]));
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_before_bounded_html_export_without_exact_bound_metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DceHtmlBeforeBound_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Guild - general [222] (before 2026-01-01).html");
        await File.WriteAllTextAsync(path, HtmlSample.Export([1000L, 2000L]));
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
