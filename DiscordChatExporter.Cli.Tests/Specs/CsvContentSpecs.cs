using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CsvContentSpecs
{
    [Fact]
    public async Task CSV_export_includes_message_id_column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-csv-header-{Guid.NewGuid():N}.csv");

        try
        {
            await using (var writer = new CsvMessageWriter(File.Create(path), null!))
            {
                await writer.WritePreambleAsync();
            }

            var header = await File.ReadAllTextAsync(path);

            header.Should().StartWith("MessageID,AuthorID,Author,Date,Content");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-1+1", "\"'-1+1\"")]
    [InlineData("@cmd", "\"'@cmd\"")]
    [InlineData("\t=1+1", "\"'\t=1+1\"")]
    [InlineData("\r=1+1", "\"'\r=1+1\"")]
    [InlineData("hello", "\"hello\"")]
    public void CSV_encoding_neutralizes_spreadsheet_formulas(string value, string expected)
    {
        var method = typeof(CsvMessageWriter).GetMethod(
            "CsvEncode",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        var encoded = (string)method!.Invoke(null, [value])!;

        encoded.Should().Be(expected);
    }

    [Fact]
    public async Task I_can_export_a_channel_in_the_CSV_format()
    {
        // Act
        var document = await ExportWrapper.ExportAsCsvAsync(ChannelIds.DateRangeTestCases);

        // Assert
        document
            .Should()
            .ContainAll(
                "tyrrrz",
                "Hello world",
                "Goodbye world",
                "Foo bar",
                "Hurdle Durdle",
                "One",
                "Two",
                "Three",
                "Yeet"
            );
    }
}
