using System;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild#guild-member-object-guild-member-flags
[Flags]
public enum MemberFlags
{
    None = 0,
    DidRejoin = 1,
    CompletedOnboarding = 2,
    BypassesVerification = 4,
    StartedOnboarding = 8,
    IsGuest = 16,
    StartedHomeActions = 32,
    CompletedHomeActions = 64,
    AutomodQuarantinedUsername = 128,
    DmSettingsUpsellAcknowledged = 512,
    AutomodQuarantinedGuildTag = 1024,
}
