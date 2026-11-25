using System;
using System.IO;
using DiscordChatExporter.Gui.Services;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Legacy_account_tokens_are_removed_when_settings_load()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"dce-settings-{Guid.NewGuid():N}.json"
        );
        File.WriteAllText(settingsPath, "{\"LastToken\":\"legacy-token\"}");

        try
        {
            var settingsService = new SettingsService(settingsPath);
            settingsService.Load().Should().BeTrue();

            settingsService.LastToken.Should().BeNull();
            File.ReadAllText(settingsPath).Should().NotContain("legacy-token");
        }
        finally
        {
            if (File.Exists(settingsPath))
                File.Delete(settingsPath);
        }
    }

    [Fact]
    public void Save_does_not_write_an_account_token()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"dce-settings-{Guid.NewGuid():N}.json"
        );

        try
        {
            var settingsService = new SettingsService(settingsPath) { LastToken = "legacy-token" };

            settingsService.Save();

            settingsService.LastToken.Should().BeNull();
            File.ReadAllText(settingsPath).Should().NotContain("legacy-token");
        }
        finally
        {
            if (File.Exists(settingsPath))
                File.Delete(settingsPath);
        }
    }
}
