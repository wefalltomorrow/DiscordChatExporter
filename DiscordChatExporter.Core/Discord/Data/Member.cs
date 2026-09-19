using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild#guild-member-object
public partial record Member(
    User User,
    string? DisplayName,
    string? AvatarUrl,
    // Guild-specific banner, which overrides the user's global one when set
    string? BannerUrl,
    IReadOnlyList<Snowflake> RoleIds,
    DateTimeOffset? JoinedAt,
    // Set only while the member is boosting the guild
    DateTimeOffset? PremiumSince,
    MemberFlags Flags,
    // True while the member has not yet passed the guild's membership screening
    bool IsPending
) : IHasId
{
    public Snowflake Id { get; } = User.Id;
}

public partial record Member
{
    // Used for users who are no longer in the guild, so none of the guild-specific data is known
    public static Member CreateFallback(User user) =>
        new(user, null, null, null, [], null, null, MemberFlags.None, false);

    public static Member Parse(JsonElement json, Snowflake? guildId = null)
    {
        var user = json.GetProperty("user").Pipe(User.Parse);
        var displayName = json.GetPropertyOrNull("nick")?.GetNonWhiteSpaceStringOrNull();

        var roleIds =
            json.GetPropertyOrNull("roles")
                ?.EnumerateArray()
                .Select(j => j.GetNonWhiteSpaceString())
                .Select(Snowflake.Parse)
                .ToArray()
            ?? [];

        var avatarUrl = guildId is not null
            ? json.GetPropertyOrNull("avatar")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetMemberAvatarUrl(guildId.Value, user.Id, h))
            : null;

        var bannerUrl = guildId is not null
            ? json.GetPropertyOrNull("banner")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetMemberBannerUrl(guildId.Value, user.Id, h))
            : null;

        var joinedAt = json.GetPropertyOrNull("joined_at")?.GetDateTimeOffsetOrNull();
        var premiumSince = json.GetPropertyOrNull("premium_since")?.GetDateTimeOffsetOrNull();

        var flags =
            json.GetPropertyOrNull("flags")?.GetInt32OrNull()?.Pipe(f => (MemberFlags)f)
            ?? MemberFlags.None;

        var isPending = json.GetPropertyOrNull("pending")?.GetBooleanOrNull() ?? false;

        return new Member(
            user,
            displayName,
            avatarUrl,
            bannerUrl,
            roleIds,
            joinedAt,
            premiumSince,
            flags,
            isPending
        );
    }
}
