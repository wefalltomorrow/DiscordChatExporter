using System;
using System.IO;
using System.Runtime.CompilerServices;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Cli.Tests;

internal static class TestEnvironment
{
    // Keeps user-token stop markers produced by the test suite out of the real
    // application-data directory, and gives every test process a fresh store.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"dce-test-user-token-stops-{Guid.NewGuid():N}"
        );
        Environment.SetEnvironmentVariable(
            UserTokenRequestStopStore.DirectoryOverrideEnvironmentVariable,
            directoryPath
        );

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(directoryPath))
                    Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}
