using System;

namespace DiscordChatExporter.Core.Exporting.Manifest;

// One catalogued output file. Self-describing: carries its own guild + channel identity
// so a single manifest can safely span multiple channels and multiple guilds.
public sealed record ManifestEntry(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string? CategoryName,
    string File,
    string Format,
    long MessageCount,
    string? FirstMessageId,
    DateTimeOffset? FirstMessageTimestamp,
    string? LastMessageId,
    DateTimeOffset? LastMessageTimestamp,
    int? AssetCount,
    long FileSizeBytes,
    string Sha256,
    bool Partitioned,
    DateTimeOffset ExportedAt
);
