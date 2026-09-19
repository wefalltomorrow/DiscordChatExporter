using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class ExportContext(DiscordClient discord, ExportRequest request, bool isOffline = false)
{
    private readonly Dictionary<Snowflake, Member?> _membersById = new();
    private readonly Dictionary<Snowflake, Color> _memberColorsById = new();
    private readonly Dictionary<Snowflake, Channel?> _channelsById = new();
    private readonly Dictionary<Snowflake, Role> _rolesById = new();
    private readonly Dictionary<
        (Snowflake? Id, string Name, bool IsAnimated),
        string
    > _emojiImageUrlsByKey = new();
    private readonly Dictionary<Snowflake, string> _emojiImageUrlsById = new();

    private readonly ExportAssetDownloader _assetDownloader = new(
        request.AssetsDirPath,
        request.ShouldReuseAssets
    );

    public DiscordClient Discord { get; } = discord;

    public ExportRequest Request { get; } = request;

    public bool IsOffline { get; } = isOffline;

    public int DownloadedAssetCount => _assetDownloader.DownloadedAssetCount;

    internal IEnumerable<KeyValuePair<Snowflake, Member?>> CachedMembers => _membersById;

    internal IEnumerable<KeyValuePair<Snowflake, Channel?>> CachedChannels => _channelsById;

    internal IEnumerable<KeyValuePair<Snowflake, Role>> CachedRoles => _rolesById;

    public DateTimeOffset NormalizeDate(DateTimeOffset instant) =>
        Request.IsUtcNormalizationEnabled ? instant.ToUniversalTime()
        : IsOffline ? instant
        : instant.ToLocalTime();

    public string FormatDate(DateTimeOffset instant, string format = "g") =>
        NormalizeDate(instant).ToString(format, Request.CultureInfo);

    public async ValueTask PopulateChannelsAndRolesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var channel in Discord.GetGuildChannelsAsync(Request.Guild.Id, cancellationToken)
        )
        {
            _channelsById[channel.Id] = channel;
        }

        await foreach (var role in Discord.GetGuildRolesAsync(Request.Guild.Id, cancellationToken))
        {
            _rolesById[role.Id] = role;
        }
    }

    // Threads are not preloaded, so we resolve them on demand
    public async ValueTask PopulateChannelAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    )
    {
        if (_channelsById.ContainsKey(id))
            return;

        if (IsOffline)
        {
            _channelsById[id] = null;
            return;
        }

        var channel = await Discord.TryGetChannelAsync(id, cancellationToken);

        // Store the result even if it's null, to avoid re-fetching non-existing channels
        _channelsById[id] = channel;
    }

    // Because members cannot be pulled in bulk, we need to populate them on demand
    private async ValueTask PopulateMemberAsync(
        Snowflake id,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    )
    {
        if (_membersById.ContainsKey(id))
            return;

        if (IsOffline)
        {
            _membersById[id] = fallbackUser is not null
                ? Member.CreateFallback(fallbackUser)
                : null;
            return;
        }

        var member = await Discord.TryGetGuildMemberAsync(Request.Guild.Id, id, cancellationToken);

        // User may have left the guild since they were mentioned.
        // Create a dummy member object based on the user info.
        if (member is null)
        {
            var user = fallbackUser ?? await Discord.TryGetUserAsync(id, cancellationToken);

            // User may have been deleted since they were mentioned
            if (user is not null)
                member = Member.CreateFallback(user);
        }

        // Store the result even if it's null, to avoid re-fetching non-existing members
        _membersById[id] = member;
    }

    public async ValueTask PopulateMemberAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(id, null, cancellationToken);

    public async ValueTask PopulateMemberAsync(
        User user,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(user.Id, user, cancellationToken);

    public Member? TryGetMember(Snowflake id) => _membersById.GetValueOrDefault(id);

    public Channel? TryGetChannel(Snowflake id) => _channelsById.GetValueOrDefault(id);

    public Role? TryGetRole(Snowflake id) => _rolesById.GetValueOrDefault(id);

    public string? TryGetEmojiImageUrl(Snowflake? id, string name, bool isAnimated) =>
        _emojiImageUrlsByKey.GetValueOrDefault((id, name, isAnimated))
        ?? (id is { } emojiId ? _emojiImageUrlsById.GetValueOrDefault(emojiId) : null);

    public IReadOnlyList<Role> GetUserRoles(Snowflake id) =>
        TryGetMember(id)
            ?.RoleIds.Select(TryGetRole)
            .WhereNotNull()
            .OrderByDescending(r => r.Position)
            .ToArray()
        ?? [];

    public Color? TryGetUserColor(Snowflake id) =>
        GetUserRoles(id).Where(r => r.Color is not null).Select(r => r.Color).FirstOrDefault()
        ?? _memberColorsById.GetValueOrDefault(id);

    public void SeedFromConversionData(ConversionData data, IEnumerable<User>? fallbackUsers = null)
    {
        var fallbackUsersById =
            fallbackUsers?.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First()) ?? [];

        foreach (var role in data.Roles)
        {
            var id = ParseSnowflake(role.Id);
            _rolesById[id] = new Role(id, role.Name, role.Position, ParseColor(role.ColorHex));
        }

        foreach (var channel in data.Channels)
        {
            var id = ParseSnowflake(channel.Id);
            var kind = ParseChannelKind(channel);
            _channelsById[id] = new Channel(
                id,
                kind,
                Request.Guild.Id,
                null,
                channel.Name,
                null,
                null,
                null,
                false,
                null
            );
        }

        foreach (var emoji in data.Emojis)
        {
            if (string.IsNullOrWhiteSpace(emoji.ImageUrl))
                continue;

            Snowflake? id = !string.IsNullOrWhiteSpace(emoji.Id) ? ParseSnowflake(emoji.Id) : null;
            _emojiImageUrlsByKey[(id, emoji.Name, emoji.IsAnimated)] = emoji.ImageUrl;

            if (id is { } emojiId)
                _emojiImageUrlsById.TryAdd(emojiId, emoji.ImageUrl);
        }

        foreach (var member in data.Members)
        {
            var id = ParseSnowflake(member.Id);
            var roleIds = member.RoleIds.Select(ParseSnowflake).ToArray();
            var fallbackUser = fallbackUsersById.GetValueOrDefault(id);
            var avatarUrl = fallbackUser?.AvatarUrl ?? member.AvatarUrl ?? "";
            var user = fallbackUser is not null
                ? fallbackUser with
                {
                    AvatarUrl = avatarUrl,
                }
                : new User(id, false, null, member.DisplayName, member.DisplayName, avatarUrl);
            _membersById[id] = new Member(user, member.DisplayName, avatarUrl, roleIds);

            if (ParseColor(member.ColorHex) is { } color)
                _memberColorsById[id] = color;
        }
    }

    private static Snowflake ParseSnowflake(string value) =>
        new(ulong.Parse(value, CultureInfo.InvariantCulture));

    private static ChannelKind ParseChannelKind(ConversionChannel channel)
    {
        if (
            !string.IsNullOrWhiteSpace(channel.Type)
            && Enum.TryParse<ChannelKind>(channel.Type, true, out var kind)
        )
        {
            return kind;
        }

        return channel.IsVoice ? ChannelKind.GuildVoiceChat : ChannelKind.GuildTextChat;
    }

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public async ValueTask<string> ResolveAssetUrlAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (!Request.ShouldDownloadAssets || !IsDownloadableAssetUrl(url))
            return url;

        try
        {
            var filePath = await _assetDownloader.DownloadAsync(url, cancellationToken);
            var relativeFilePath = Path.GetRelativePath(Request.OutputDirPath, filePath);

            // Prefer the relative path so that the export package can be copied around without breaking references.
            // However, if the assets directory lies outside the export directory, use the absolute path instead.
            var shouldUseAbsoluteFilePath =
                relativeFilePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
                || relativeFilePath.StartsWith(
                    ".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal
                );

            var optimalFilePath = shouldUseAbsoluteFilePath ? filePath : relativeFilePath;

            // For HTML, the path needs to be properly formatted
            if (Request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight)
                return Url.EncodeFilePath(optimalFilePath);

            return optimalFilePath;
        }
        // Try to catch only exceptions related to failed HTTP requests or local asset IO failures
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
        catch (Exception ex)
            when (ex is HttpRequestException
                || ex is IOException
                || ex is UnauthorizedAccessException
                || ex is PathTooLongException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
            )
        {
            // We don't want this to crash the exporting process in case of failure.
            // TODO: add logging so we can be more liberal with catching exceptions.
            return url;
        }
    }

    private static bool IsDownloadableAssetUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
