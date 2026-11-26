using System.IO;
using DiscordChatExporter.Gui;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class StartOptionsTests
{
    [Fact]
    public void Default_settings_path_uses_per_user_appdata()
    {
        // Build paths with Path.Combine so the test asserts the resolution logic, not a particular
        // platform's separator: ResolveSettingsPath also uses Path.Combine, which yields '/' on Linux
        // and '\' on Windows. A hardcoded backslash literal fails on the Linux CI runner.
        var localAppData = Path.Combine(Path.GetTempPath(), "Users", "Alice", "AppData", "Local");

        var settingsPath = StartOptions.ResolveSettingsPath(
            null,
            null,
            Path.Combine(Path.GetTempPath(), "Portable", "App"),
            localAppData
        );

        settingsPath.Should().Be(Path.Combine(localAppData, "DiscordChatExporter", "Settings.dat"));
    }

    [Fact]
    public void Settings_path_override_uses_file_path()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            @"C:\Settings\Custom.dat",
            null,
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Settings\Custom.dat");
    }

    [Fact]
    public void Settings_path_override_appends_file_name_for_directory_path()
    {
        // A path that ends in the platform's directory separator is treated as a directory and the
        // settings file name is appended. Path.DirectorySeparatorChar keeps this true on Linux, where
        // a trailing backslash is an ordinary filename character rather than a separator.
        var directoryPath =
            Path.Combine(Path.GetTempPath(), "Settings") + Path.DirectorySeparatorChar;

        var settingsPath = StartOptions.ResolveSettingsPath(
            directoryPath,
            null,
            Path.Combine(Path.GetTempPath(), "Portable", "App"),
            Path.Combine(Path.GetTempPath(), "Users", "Alice", "AppData", "Local")
        );

        settingsPath.Should().Be(Path.Combine(directoryPath, "Settings.dat"));
    }

    [Fact]
    public void Portable_mode_uses_executable_directory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Portable", "App");

        var settingsPath = StartOptions.ResolveSettingsPath(
            null,
            "true",
            baseDirectory,
            Path.Combine(Path.GetTempPath(), "Users", "Alice", "AppData", "Local")
        );

        settingsPath.Should().Be(Path.Combine(baseDirectory, "Settings.dat"));
    }

    [Fact]
    public void Settings_path_override_wins_over_portable_mode()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            @"C:\Settings\Custom.dat",
            "true",
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Settings\Custom.dat");
    }
}
