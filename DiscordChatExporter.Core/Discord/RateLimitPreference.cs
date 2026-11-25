using System;

namespace DiscordChatExporter.Core.Discord;

[Flags]
public enum RateLimitPreference
{
    IgnoreAll = 0,
    RespectForBotTokens = 0b1,
    RespectAll = RespectForBotTokens,
}

public static class RateLimitPreferenceExtensions
{
    extension(RateLimitPreference rateLimitPreference)
    {
        internal bool IsRespectedFor(TokenKind tokenKind) => true;

        public string GetDisplayName() => "Respect Discord rate limits";
    }
}
