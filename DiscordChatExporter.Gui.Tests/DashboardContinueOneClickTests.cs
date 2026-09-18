using System.Collections.Generic;
using System.Reflection;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DashboardContinueOneClickTests
{
    private static bool ShouldPromptAnchorPicker(ContinueDiscoveryResult result)
    {
        var method = typeof(DashboardViewModel).GetMethod(
            "ShouldPromptAnchorPicker",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method.Should().NotBeNull();
        return (bool)method!.Invoke(null, [result])!;
    }

    [Fact]
    public void Does_not_prompt_picker_when_catalog_resolves_at_least_one_channel()
    {
        var result = new ContinueDiscoveryResult(
            [new ResolvedCatalogEntry(new Snowflake(1), "export.json", ExportFormat.Json)],
            []
        );

        ShouldPromptAnchorPicker(result).Should().BeFalse();
    }

    [Fact]
    public void Prompts_picker_when_catalog_resolves_zero_channels()
    {
        var result = new ContinueDiscoveryResult(
            [],
            [new UnresolvedCatalogChannel(new Snowflake(1), ContinueSkipReason.NoPriorExport)]
        );

        ShouldPromptAnchorPicker(result).Should().BeTrue();
    }
}
