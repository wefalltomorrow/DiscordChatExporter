using System;
using System.IO;

namespace DiscordChatExporter.Gui;

public partial class StartOptions
{
    public required string SettingsPath { get; init; }

    public required bool IsAutoUpdateAllowed { get; init; }
}

public partial class StartOptions
{
    private const string SettingsFileName = "Settings.dat";
    private const string SettingsDirectoryName = "DiscordChatExporter";
    private const string SettingsPathEnvVar = "DISCORDCHATEXPORTER_SETTINGS_PATH";
    private const string PortableModeEnvVar = "DISCORDCHATEXPORTER_PORTABLE";

    public static StartOptions Current { get; } =
        new()
        {
            SettingsPath = ResolveSettingsPath(
                Environment.GetEnvironmentVariable(SettingsPathEnvVar),
                Environment.GetEnvironmentVariable(PortableModeEnvVar),
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            ),
            IsAutoUpdateAllowed = !(
                Environment.GetEnvironmentVariable("DISCORDCHATEXPORTER_ALLOW_AUTO_UPDATE")
                    is { } env
                && env.Equals("false", StringComparison.OrdinalIgnoreCase)
            ),
        };

    internal static string ResolveSettingsPath(
        string? configuredPath,
        string? portableMode,
        string baseDirectory,
        string localAppDataDirectory
    )
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return ResolveConfiguredSettingsPath(configuredPath);

        if (IsPortableModeEnabled(portableMode))
            return Path.Combine(baseDirectory, SettingsFileName);

        var settingsRoot = !string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? localAppDataDirectory
            : baseDirectory;

        return Path.Combine(settingsRoot, SettingsDirectoryName, SettingsFileName);
    }

    private static string ResolveConfiguredSettingsPath(string configuredPath)
    {
        var path = configuredPath.Trim();

        return Path.EndsInDirectorySeparator(path) || Directory.Exists(path)
            ? Path.Combine(path, SettingsFileName)
            : path;
    }

    private static bool IsPortableModeEnabled(string? portableMode) =>
        portableMode is not null
        && (
            portableMode.Equals("1", StringComparison.OrdinalIgnoreCase)
            || portableMode.Equals("true", StringComparison.OrdinalIgnoreCase)
            || portableMode.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || portableMode.Equals("on", StringComparison.OrdinalIgnoreCase)
        );
}
