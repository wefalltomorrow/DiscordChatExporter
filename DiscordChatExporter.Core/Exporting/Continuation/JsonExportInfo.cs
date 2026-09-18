using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public sealed record JsonExportInfo(
    Snowflake GuildId,
    Snowflake ChannelId,
    Snowflake? Before,
    Snowflake LastMessageId,
    long MessageCount,
    bool IsChronological
);
