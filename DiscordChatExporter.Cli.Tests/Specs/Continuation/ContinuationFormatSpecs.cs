using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class ContinuationFormatSpecs
{
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

    [Fact]
    public async Task Json_resume_prefers_in_file_channel_id_over_filename()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"Guild [333] - channel [333] {Guid.NewGuid():N}.json"
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
            var cutoff = await ContinuationFormat.ReadCutoffAsync(path);

            cutoff.ChannelId.Value.Should().Be(222UL);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
