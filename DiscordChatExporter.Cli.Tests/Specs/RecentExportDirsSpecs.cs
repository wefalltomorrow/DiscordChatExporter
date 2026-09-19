using DiscordChatExporter.Core.Exporting.Library;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class RecentExportDirsSpecs
{
    [Fact]
    public void Adds_a_new_dir_to_the_front()
    {
        RecentExportDirs.Add(["b", "c"], "a", 10).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Moves_an_existing_dir_to_the_front_without_duplicating()
    {
        RecentExportDirs.Add(["a", "b", "c"], "c", 10).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void Is_case_insensitive_on_paths()
    {
        RecentExportDirs.Add(["A", "b"], "a", 10).Should().Equal("a", "b");
    }

    [Fact]
    public void Caps_the_list_at_the_max()
    {
        RecentExportDirs.Add(["b", "c", "d"], "a", 3).Should().Equal("a", "b", "c");
    }
}
