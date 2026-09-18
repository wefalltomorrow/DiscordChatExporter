namespace DiscordChatExporter.Core.Exporting.Manifest;

// The channel/guild identity for a manifest entry, supplied by the caller (which holds the
// ExportRequest). Keeps ManifestBuilder decoupled from the Discord data types so it is trivially
// unit-testable.
public sealed record ManifestChannelInfo(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string? CategoryName,
    string Format
);
