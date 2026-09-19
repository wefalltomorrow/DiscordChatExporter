using System;
using System.Collections.Generic;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

// Stats for a single output file produced by an export (one channel may produce
// several files when partitioning is enabled).
public sealed record ExportedFile(
    string FilePath,
    long MessageCount,
    Snowflake? FirstMessageId,
    DateTimeOffset? FirstMessageTimestamp,
    Snowflake? LastMessageId,
    DateTimeOffset? LastMessageTimestamp
);

// The outcome of exporting one channel.
public sealed record ExportResult(
    IReadOnlyList<ExportedFile> Files,
    long MessageCount,
    int AssetCount
);
