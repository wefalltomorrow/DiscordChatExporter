using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Versioned metadata captured in JSON exports to preserve offline conversion fidelity.
public sealed record ConversionData(
    IReadOnlyList<ConversionMember> Members,
    IReadOnlyList<ConversionRole> Roles,
    IReadOnlyList<ConversionChannel> Channels,
    IReadOnlyList<ConversionEmoji> Emojis
)
{
    public const int CurrentSchemaVersion = 1;

    public ConversionData(
        IReadOnlyList<ConversionMember> members,
        IReadOnlyList<ConversionRole> roles,
        IReadOnlyList<ConversionChannel> channels
    )
        : this(members, roles, channels, []) { }
}

// Guild member metadata used when rendering converted exports offline.
public sealed record ConversionMember(
    string Id,
    string DisplayName,
    string? AvatarUrl,
    string? ColorHex,
    IReadOnlyList<string> RoleIds
);

// Guild role metadata used for offline author color and role reconstruction.
public sealed record ConversionRole(string Id, string Name, string? ColorHex, int Position);

// Channel metadata used to resolve mentions and converted export context offline.
public sealed record ConversionChannel(
    string Id,
    string Name,
    string? Type = null,
    bool IsVoice = false
);

// Custom emoji metadata used to render downloaded emoji assets in converted exports.
public sealed record ConversionEmoji(string? Id, string Name, bool IsAnimated, string ImageUrl);
