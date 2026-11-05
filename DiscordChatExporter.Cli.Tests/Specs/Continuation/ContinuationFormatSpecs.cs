using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class ContinuationFormatSpecs
{
    [Theory]
    [InlineData(ExportFormat.HtmlDark)]
    [InlineData(ExportFormat.HtmlLight)]
    public async Task Both_html_formats_dispatch_reading_and_merging_with_custom_extensions(
        ExportFormat format
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var existing = Path.Combine(dir, "existing [222].custom");
            var incoming = Path.Combine(dir, "incoming [222].custom");
            await File.WriteAllTextAsync(existing, HtmlSample.Export([1000L]));
            await File.WriteAllTextAsync(incoming, HtmlSample.Export([2000L]));
            var cutoff = await ContinuationFormat.ReadCutoffAsync(existing, format);
            var total = await ContinuationFormat.MergeAsync(
                existing,
                incoming,
                cutoff,
                DateTimeOffset.UnixEpoch,
                format
            );
            total.Should().Be(2);
            (await ContinuationFormat.ReadCutoffAsync(existing, format))
                .Cutoff.Value.Should()
                .Be(2000UL);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("x.json", true)]
    [InlineData("x.html", true)]
    [InlineData("x.htm", true)]
    [InlineData("x.csv", true)]
    [InlineData("x.db", true)]
    [InlineData("x.txt", false)]
    [InlineData("x.xml", false)]
    public void It_recognizes_supported_extensions(string path, bool supported)
    {
        ContinuationFormat.IsSupportedExtension(path).Should().Be(supported);
    }

    [Theory]
    [InlineData("x.json", ExportFormat.Json)]
    [InlineData("x.html", ExportFormat.HtmlDark)]
    [InlineData("x.htm", ExportFormat.HtmlDark)]
    [InlineData("x.csv", ExportFormat.Csv)]
    [InlineData("x.db", ExportFormat.Db)]
    public void It_maps_supported_extensions_to_export_formats(
        string path,
        ExportFormat expectedFormat
    )
    {
        ContinuationFormat.FormatFor(path).Should().Be(expectedFormat);
    }

    [Fact]
    public void Invalid_json_export_errors_are_invalid_export_errors()
    {
        new InvalidJsonExportException("bad").Should().BeAssignableTo<InvalidExportException>();
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".html")]
    [InlineData(".custom")]
    public async Task Json_resume_and_merge_use_explicit_format_and_in_file_channel_id(
        string extension
    )
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"Guild [333] - channel [333] {Guid.NewGuid():N}{extension}"
        );
        await File.WriteAllTextAsync(
            path,
            """
            {
              "guild": { "id": "111", "name": "G" },
              "channel": { "id": "222", "name": "C" },
              "dateRange": { "after": null, "before": null },
              "messages": [
                { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" }
              ],
              "messageCount": 1
            }
            """
        );

        try
        {
            var cutoff = await ContinuationFormat.ReadCutoffAsync(path, ExportFormat.Json);

            cutoff.ChannelId.Value.Should().Be(222UL);
            var incomingPath = path + ".incoming";
            try
            {
                await File.WriteAllTextAsync(
                    incomingPath,
                    (await File.ReadAllTextAsync(path)).Replace("1000", "2000")
                );
                var total = await ContinuationFormat.MergeAsync(
                    path,
                    incomingPath,
                    cutoff,
                    DateTimeOffset.UnixEpoch,
                    ExportFormat.Json
                );
                total.Should().Be(2);
                var merged = await ContinuationFormat.ReadCutoffAsync(path, ExportFormat.Json);
                merged.ExistingCount.Should().Be(2);
                merged.Cutoff.Value.Should().Be(2000UL);
            }
            finally
            {
                File.Delete(incomingPath);
                File.Delete(path + ".bak");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Picked_file_without_manifest_keeps_extension_fallback()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "export.json");
        (await ContinuationFormat.ResolveFormatAsync(path)).Should().Be(ExportFormat.Json);
    }
}
