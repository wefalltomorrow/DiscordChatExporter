using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord.Data.Common;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/guild#guild-object
public partial record Guild(
    Snowflake Id,
    string Name,
    string IconUrl,
    Snowflake? OwnerId,
    string? Description,
    string? BannerUrl,
    string? SplashUrl,
    string? VanityUrl,
    // Boost level (0-3) and the number of boosts backing it
    int? PremiumTier,
    int? PremiumSubscriptionCount,
    // Only returned when the guild is fetched with the 'with_counts' parameter
    int? ApproximateMemberCount,
    int? ApproximatePresenceCount,
    // The guild's full inventory, as opposed to just whatever the exported messages happen to use.
    // Absent from the abbreviated guild objects returned by the guild list endpoint.
    IReadOnlyList<Role> Roles,
    IReadOnlyList<Emoji> Emojis,
    IReadOnlyList<Sticker> Stickers
) : IHasId
{
    public bool IsDirect { get; } = Id == Snowflake.Zero;
}

public partial record Guild
{
    // Direct messages are encapsulated within a special pseudo-guild for consistency
    public static Guild DirectMessages { get; } =
        new(
            Snowflake.Zero,
            "Direct Messages",
            ImageCdn.GetFallbackUserAvatarUrl(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            []
        );

    public static Guild Parse(JsonElement json)
    {
        var id = json.GetProperty("id").GetNonWhiteSpaceString().Pipe(Snowflake.Parse);
        var name = json.GetProperty("name").GetNonNullString();

        var iconUrl =
            json.GetPropertyOrNull("icon")
                ?.GetNonWhiteSpaceStringOrNull()
                ?.Pipe(h => ImageCdn.GetGuildIconUrl(id, h))
            ?? ImageCdn.GetFallbackUserAvatarUrl();

        var ownerId = json.GetPropertyOrNull("owner_id")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(Snowflake.Parse);

        var description = json.GetPropertyOrNull("description")?.GetNonWhiteSpaceStringOrNull();

        var bannerUrl = json.GetPropertyOrNull("banner")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetGuildBannerUrl(id, h));

        var splashUrl = json.GetPropertyOrNull("splash")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(h => ImageCdn.GetGuildSplashUrl(id, h));

        var vanityUrl = json.GetPropertyOrNull("vanity_url_code")
            ?.GetNonWhiteSpaceStringOrNull()
            ?.Pipe(c => $"https://discord.gg/{c}");

        var premiumTier = json.GetPropertyOrNull("premium_tier")?.GetInt32OrNull();

        var premiumSubscriptionCount = json.GetPropertyOrNull("premium_subscription_count")
            ?.GetInt32OrNull();

        var approximateMemberCount = json.GetPropertyOrNull("approximate_member_count")
            ?.GetInt32OrNull();

        var approximatePresenceCount = json.GetPropertyOrNull("approximate_presence_count")
            ?.GetInt32OrNull();

        var roles =
            json.GetPropertyOrNull("roles")?.EnumerateArrayOrNull()?.Select(Role.Parse).ToArray()
            ?? [];

        var emojis =
            json.GetPropertyOrNull("emojis")?.EnumerateArrayOrNull()?.Select(Emoji.Parse).ToArray()
            ?? [];

        var stickers =
            json.GetPropertyOrNull("stickers")
                ?.EnumerateArrayOrNull()
                ?.Select(Sticker.Parse)
                .ToArray()
            ?? [];

        return new Guild(
            id,
            name,
            iconUrl,
            ownerId,
            description,
            bannerUrl,
            splashUrl,
            vanityUrl,
            premiumTier,
            premiumSubscriptionCount,
            approximateMemberCount,
            approximatePresenceCount,
            roles,
            emojis,
            stickers
        );
    }
}
