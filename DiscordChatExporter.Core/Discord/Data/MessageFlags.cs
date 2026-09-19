using System;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/channel#message-object-message-flags
[Flags]
public enum MessageFlags
{
    None = 0,
    CrossPosted = 1,
    CrossPost = 2,
    SuppressEmbeds = 4,
    SourceMessageDeleted = 8,
    Urgent = 16,
    HasThread = 32,
    Ephemeral = 64,
    Loading = 128,
    FailedToMentionSomeRolesInThread = 256,
    ShouldShowLinkNotDiscordWarning = 1024,
    SuppressNotifications = 4096,
    IsVoiceMessage = 8192,
    HasSnapshot = 16384,

    // Marks a message that carries its content in the component tree instead of in 'content',
    // which is why such a message looks empty to anything that only reads the latter
    IsComponentsV2 = 32768,
}
