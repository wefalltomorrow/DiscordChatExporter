using System;
using System.IO;
using DiscordChatExporter.Gui.Services;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Token_persistence_is_opt_out_by_default()
    {
        var settingsService = new SettingsService();

        settingsService.IsTokenPersisted.Should().BeTrue();
    }

    [Fact]
    public void Failed_save_restores_non_persisted_token_in_memory()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"dce-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsPath);

        try
        {
            var settingsService = new SettingsService(settingsPath)
            {
                IsTokenPersisted = false,
                LastToken = "token",
            };

            var act = settingsService.Save;

            act.Should().Throw<Exception>();
            settingsService.LastToken.Should().Be("token");
        }
        finally
        {
            Directory.Delete(settingsPath, true);
        }
    }
}
