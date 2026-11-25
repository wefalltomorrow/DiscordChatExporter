using Xunit.Sdk;

namespace DiscordChatExporter.Cli.Tests.Infra;

internal static class Secrets
{
    public static string DiscordToken =>
        throw SkipException.ForSkip(
            "Live Discord integration tests are disabled in this offline-only build."
        );
}
