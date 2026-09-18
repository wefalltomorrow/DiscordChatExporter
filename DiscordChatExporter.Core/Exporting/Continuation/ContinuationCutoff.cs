using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public sealed record ContinuationCutoff(
    Snowflake ChannelId,
    Snowflake Cutoff,
    Snowflake? Before,
    bool IsChronological,
    long ExistingCount,
    bool CutoffIsExact
);
