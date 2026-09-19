using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Platform.Storage;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DashboardContinueExportPickerTests
{
    [Fact]
    public void Continue_export_picker_accepts_sqlite_exports()
    {
        var method = typeof(DashboardViewModel).GetMethod(
            "CreateContinueExportFileTypes",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method.Should().NotBeNull();
        var fileTypes = ((IReadOnlyList<FilePickerFileType>)method!.Invoke(null, null)!)
            .Should()
            .ContainSingle()
            .Subject;

        fileTypes.Name.Should().Be("Supported exports (JSON, HTML, CSV, SQLite)");
        fileTypes.Patterns.Should().BeEquivalentTo(["*.json", "*.html", "*.htm", "*.csv", "*.db"]);
    }

    [Fact]
    public void Continue_export_localization_mentions_sqlite_exports()
    {
        using var localization = new LocalizationManager(new SettingsService());

        localization.ContinueExportTooltip.Should().Contain("SQLite");
        localization.ContinueExportFormatUnsupportedMessage.Should().Contain("SQLite");
    }
}
