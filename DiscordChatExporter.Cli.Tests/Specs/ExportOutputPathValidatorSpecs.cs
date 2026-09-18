using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportOutputPathValidatorSpecs
{
    [Fact]
    public void Case_only_output_paths_are_distinct_with_a_case_sensitive_comparer()
    {
        var duplicatePaths = ExportOutputPathValidator.GetDuplicateOutputFilePaths(
            ["C:\\Exports\\chat.csv", "C:\\Exports\\CHAT.csv"],
            _ => StringComparer.Ordinal
        );

        duplicatePaths.Should().BeEmpty();
    }

    [Fact]
    public void Case_only_output_paths_conflict_with_a_case_insensitive_comparer()
    {
        var duplicatePaths = ExportOutputPathValidator.GetDuplicateOutputFilePaths(
            ["C:\\Exports\\chat.csv", "C:\\Exports\\CHAT.csv"],
            _ => StringComparer.OrdinalIgnoreCase
        );

        duplicatePaths.Should().BeEquivalentTo(["C:\\Exports\\chat.csv", "C:\\Exports\\CHAT.csv"]);
    }
}
