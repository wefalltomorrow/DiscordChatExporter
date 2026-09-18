using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportFormatSpecs
{
    [Fact]
    public void Db_format_has_a_db_file_extension()
    {
        ExportFormat.Db.GetFileExtension().Should().Be("db");
    }

    [Fact]
    public void Db_format_has_a_display_name()
    {
        ExportFormat.Db.GetDisplayName().Should().Be("SQLite");
    }
}
