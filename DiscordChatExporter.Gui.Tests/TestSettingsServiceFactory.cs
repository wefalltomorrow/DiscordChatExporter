using System;
using System.IO;
using DiscordChatExporter.Gui.Services;

namespace DiscordChatExporter.Gui.Tests;

internal static class TestSettingsServiceFactory
{
    public static SettingsService Create() =>
        new(Path.Combine(Path.GetTempPath(), $"dce-gui-settings-{Guid.NewGuid():N}.dat"));
}
