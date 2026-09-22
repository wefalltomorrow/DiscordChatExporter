using System;

namespace DiscordChatExporter.Core.Discord;

[Flags]
public enum RateLimitPreference
{
    IgnoreAll = 0,
    RespectForUserTokens = 0b1,
    RespectForBotTokens = 0b10,
    RespectAll = RespectForUserTokens | RespectForBotTokens,
}

public static class RateLimitPreferenceExtensions
{
    extension(RateLimitPreference rateLimitPreference)
    {
        internal bool IsRespectedFor(TokenKind tokenKind) =>
            tokenKind switch
            {
                // User-token traffic is always conservative. Keep the enum shape for API
                // compatibility, but never allow callers to opt user requests out of Discord's
                // advisory limits.
                TokenKind.User => true,
                TokenKind.Bot => (rateLimitPreference & RateLimitPreference.RespectForBotTokens)
                    != 0,
                _ => throw new ArgumentOutOfRangeException(nameof(tokenKind)),
            };

        public string GetDisplayName() =>
            rateLimitPreference switch
            {
                RateLimitPreference.IgnoreAll => "Ignore for bot tokens only",
                RateLimitPreference.RespectForUserTokens => "Respect for user tokens",
                RateLimitPreference.RespectForBotTokens => "Respect for bot tokens",
                RateLimitPreference.RespectAll => "Always respect",
                _ => throw new ArgumentOutOfRangeException(nameof(rateLimitPreference)),
            };
    }
}
