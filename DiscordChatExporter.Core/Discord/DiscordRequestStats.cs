namespace DiscordChatExporter.Core.Discord;

public readonly record struct DiscordRequestStats(
    long RequestCount,
    long AvoidedUnavailableRequestCount,
    long HardRateLimitCount,
    long AdvisoryPauseCount
);
