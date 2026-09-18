using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Exporting.Continuation;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Parsed JSON export payload used as the source model for offline conversion.
public sealed record ParsedExport(
    Guild Guild,
    Channel Channel,
    IReadOnlyList<Message> Messages,
    ConversionData? ConversionData,
    bool HasConversionDataBlock,
    Snowflake? After,
    Snowflake? Before
);

// Reads DiscordChatExporter JSON files back into export models for conversion.
public static class JsonExportReader
{
    public static async ValueTask<ParsedExport> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            throw new InvalidExportException($"The file '{filePath}' does not exist.");

        try
        {
            await using var stream = File.OpenRead(filePath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );
            var root = document.RootElement;

            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("guild", out var guildJson)
                || !root.TryGetProperty("channel", out var channelJson)
                || !root.TryGetProperty("messages", out var messagesJson)
                || messagesJson.ValueKind != JsonValueKind.Array
            )
            {
                throw new InvalidExportException(
                    "This file is not a DiscordChatExporter JSON export."
                );
            }

            var guild = ParseGuild(guildJson);
            var channel = ParseChannel(channelJson, guild.Id);
            var (after, before) = root.TryGetProperty("dateRange", out var dateRangeJson)
                ? ParseDateRange(dateRangeJson)
                : (null, null);
            var (messages, inlineEmojis, legacyConversionData) = ParseMessages(messagesJson);
            var hasConversionDataBlock = root.TryGetProperty(
                "conversionData",
                out var conversionDataJson
            );
            var conversionData = hasConversionDataBlock
                ? ParseConversionData(conversionDataJson)
                : null;
            conversionData = MergeConversionData(
                conversionData,
                legacyConversionData,
                inlineEmojis
            );

            return new ParsedExport(
                guild,
                channel,
                messages,
                conversionData,
                hasConversionDataBlock,
                after,
                before
            );
        }
        catch (InvalidExportException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or FormatException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or OverflowException
            )
        {
            throw new InvalidExportException($"'{filePath}' is not a valid JSON chat export.", ex);
        }
    }

    private static Guild ParseGuild(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            GetAssetUrl(json, "iconUrl")
        );

    private static Channel ParseChannel(JsonElement json, Snowflake guildId)
    {
        var parent = GetStringOrNull(json, "categoryId") is { } categoryId
            ? new Channel(
                ParseSnowflake(categoryId),
                ChannelKind.GuildCategory,
                guildId,
                null,
                GetStringOrNull(json, "category") ?? categoryId,
                null,
                null,
                null,
                false,
                null
            )
            : null;

        return new Channel(
            ParseSnowflake(json.GetProperty("id")),
            ParseEnum(GetString(json, "type"), ChannelKind.GuildTextChat),
            guildId,
            parent,
            GetString(json, "name"),
            null,
            GetAssetUrlOrNull(json, "iconUrl"),
            GetStringOrNull(json, "topic"),
            false,
            null
        );
    }

    private static Message ParseMessage(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            ParseEnum(GetString(json, "type"), MessageKind.Default),
            (MessageFlags)GetInt32(json, "flags"),
            ParseUser(json.GetProperty("author")),
            ParseDate(GetString(json, "timestamp")),
            ParseDateOrNull(json, "timestampEdited"),
            ParseDateOrNull(json, "callEndedTimestamp"),
            GetBoolean(json, "isPinned"),
            GetStringOrNull(json, "contentRaw") ?? GetString(json, "content"),
            ParseArray(json, "attachments", ParseAttachment),
            ParseArray(json, "embeds", ParseEmbed),
            ParseArray(json, "stickers", ParseSticker),
            ParseArray(json, "reactions", ParseReaction),
            ParseArray(json, "mentions", ParseUser),
            json.TryGetProperty("reference", out var referenceJson)
                ? ParseMessageReference(referenceJson)
                : null,
            json.TryGetProperty("referencedMessage", out var referencedJson)
                ? ParseMessage(referencedJson)
                : null,
            json.TryGetProperty("forwardedMessage", out var forwardedJson)
                ? ParseMessageSnapshot(forwardedJson)
                : null,
            json.TryGetProperty("interaction", out var interactionJson)
                ? ParseInteraction(interactionJson)
                : null
        );

    private static User ParseUser(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetBoolean(json, "isBot"),
            int.TryParse(GetStringOrNull(json, "discriminator"), out var discriminator)
            && discriminator > 0
                ? discriminator
                : null,
            GetString(json, "name"),
            GetStringOrNull(json, "nickname") ?? GetString(json, "name"),
            GetAssetUrl(json, "avatarUrl")
        );

    private static Attachment ParseAttachment(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetAssetUrl(json, "url"),
            GetString(json, "fileName"),
            GetStringOrNull(json, "description"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height"),
            FileSize.FromBytes(GetInt64(json, "fileSizeBytes"))
        );

    private static Embed ParseEmbed(JsonElement json) =>
        new(
            GetStringOrNull(json, "titleRaw") ?? GetStringOrNull(json, "title"),
            ParseEnum(GetStringOrNull(json, "type"), InferLegacyEmbedKind(json)),
            GetStringOrNull(json, "url"),
            ParseDateOrNull(json, "timestamp"),
            ParseColor(GetStringOrNull(json, "color")),
            json.TryGetProperty("author", out var author) ? ParseEmbedAuthor(author) : null,
            GetStringOrNull(json, "descriptionRaw") ?? GetStringOrNull(json, "description"),
            ParseArray(json, "fields", ParseEmbedField),
            json.TryGetProperty("thumbnail", out var thumbnail) ? ParseEmbedImage(thumbnail) : null,
            ParseEmbedImages(json),
            json.TryGetProperty("video", out var video) ? ParseEmbedVideo(video) : null,
            json.TryGetProperty("footer", out var footer) ? ParseEmbedFooter(footer) : null
        );

    private static EmbedKind InferLegacyEmbedKind(JsonElement json)
    {
        if (json.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            return HasGifvUrl(json) ? EmbedKind.Gifv : EmbedKind.Video;

        if (HasImage(json) && IsBareMediaEmbed(json))
            return EmbedKind.Image;

        return !string.IsNullOrWhiteSpace(GetStringOrNull(json, "url"))
            ? EmbedKind.Link
            : EmbedKind.Rich;
    }

    private static bool HasGifvUrl(JsonElement json)
    {
        var urls = new[]
        {
            GetStringOrNull(json, "url"),
            json.TryGetProperty("video", out var video) ? GetStringOrNull(video, "url") : null,
            json.TryGetProperty("video", out video) ? GetStringOrNull(video, "canonicalUrl") : null,
        };

        return urls.Any(url =>
            url?.EndsWith(".gifv", StringComparison.OrdinalIgnoreCase) == true
            || url?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true
        );
    }

    private static bool HasImage(JsonElement json) =>
        json.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
        || json.TryGetProperty("thumbnail", out var thumbnail)
            && thumbnail.ValueKind == JsonValueKind.Object
        || json.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0;

    private static bool IsBareMediaEmbed(JsonElement json) =>
        string.IsNullOrWhiteSpace(GetStringOrNull(json, "title"))
        && string.IsNullOrWhiteSpace(GetStringOrNull(json, "description"))
        && !HasNonNullProperty(json, "author")
        && !HasNonNullProperty(json, "footer")
        && !HasNonNullProperty(json, "color")
        && !HasNonNullProperty(json, "timestamp")
        && (
            !json.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0
        );

    private static bool HasNonNullProperty(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null;

    private static EmbedAuthor ParseEmbedAuthor(JsonElement json) =>
        new(
            GetStringOrNull(json, "name"),
            GetStringOrNull(json, "url"),
            GetAssetUrlOrNull(json, "iconCanonicalUrl") ?? GetAssetUrlOrNull(json, "iconUrl"),
            GetAssetUrlOrNull(json, "iconUrl")
        );

    private static EmbedImage ParseEmbedImage(JsonElement json) =>
        new(
            GetAssetUrlOrNull(json, "canonicalUrl") ?? GetAssetUrlOrNull(json, "url"),
            GetAssetUrlOrNull(json, "url"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height")
        );

    private static IReadOnlyList<EmbedImage> ParseEmbedImages(JsonElement json)
    {
        var images = new List<EmbedImage>();

        if (
            json.TryGetProperty("image", out var imageJson)
            && imageJson.ValueKind == JsonValueKind.Object
        )
        {
            images.Add(ParseEmbedImage(imageJson));
        }

        foreach (var imageItem in EnumerateArray(json, "images"))
        {
            var parsedImage = ParseEmbedImage(imageItem);
            if (!images.Any(existing => HasSameImageSource(existing, parsedImage)))
                images.Add(parsedImage);
        }

        return images;
    }

    private static bool HasSameImageSource(EmbedImage left, EmbedImage right) =>
        string.Equals(left.Url, right.Url, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ProxyUrl, right.ProxyUrl, StringComparison.OrdinalIgnoreCase);

    private static EmbedVideo ParseEmbedVideo(JsonElement json) =>
        new(
            GetAssetUrlOrNull(json, "canonicalUrl") ?? GetAssetUrlOrNull(json, "url"),
            GetAssetUrlOrNull(json, "url"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height")
        );

    private static EmbedFooter ParseEmbedFooter(JsonElement json) =>
        new(
            GetString(json, "text"),
            GetAssetUrlOrNull(json, "iconCanonicalUrl") ?? GetAssetUrlOrNull(json, "iconUrl"),
            GetAssetUrlOrNull(json, "iconUrl")
        );

    private static EmbedField ParseEmbedField(JsonElement json) =>
        new(
            GetStringOrNull(json, "nameRaw") ?? GetString(json, "name"),
            GetStringOrNull(json, "valueRaw") ?? GetString(json, "value"),
            GetBoolean(json, "isInline")
        );

    private static Sticker ParseSticker(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            ParseEnum(GetString(json, "format"), StickerFormat.Png),
            GetAssetUrl(json, "sourceUrl")
        );

    private static Reaction ParseReaction(JsonElement json) =>
        new(ParseEmoji(json.GetProperty("emoji")), GetInt32(json, "count"));

    private static Emoji ParseEmoji(JsonElement json) =>
        new Emoji(
            GetStringOrNull(json, "id") is { } id && !string.IsNullOrWhiteSpace(id)
                ? ParseSnowflake(id)
                : null,
            GetString(json, "name"),
            GetBoolean(json, "isAnimated")
        )
        {
            ImageUrlOverride = GetAssetUrlOrNull(json, "imageUrl"),
        };

    private static MessageReference ParseMessageReference(JsonElement json) =>
        new(
            ParseEnum(GetString(json, "type"), MessageReferenceKind.Default),
            GetStringOrNull(json, "messageId") is { } messageId ? ParseSnowflake(messageId) : null,
            GetStringOrNull(json, "channelId") is { } channelId ? ParseSnowflake(channelId) : null,
            GetStringOrNull(json, "guildId") is { } guildId ? ParseSnowflake(guildId) : null
        );

    private static MessageSnapshot ParseMessageSnapshot(JsonElement json) =>
        new(
            ParseDate(GetString(json, "timestamp")),
            ParseDateOrNull(json, "timestampEdited"),
            GetStringOrNull(json, "contentRaw") ?? GetString(json, "content"),
            ParseArray(json, "attachments", ParseAttachment),
            ParseArray(json, "embeds", ParseEmbed),
            ParseArray(json, "stickers", ParseSticker)
        );

    private static Interaction ParseInteraction(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            ParseUser(json.GetProperty("user"))
        );

    private static ConversionData ParseConversionData(JsonElement json)
    {
        if (json.TryGetProperty("schemaVersion", out var schemaVersionJson))
        {
            int schemaVersion;
            try
            {
                schemaVersion = schemaVersionJson.GetInt32();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                throw new InvalidExportException(
                    "The conversionData.schemaVersion value is malformed.",
                    ex
                );
            }

            if (schemaVersion != ConversionData.CurrentSchemaVersion)
            {
                throw new InvalidExportException(
                    "The conversionData.schemaVersion value is not supported."
                );
            }
        }

        return new ConversionData(
            ParseArray(
                json,
                "members",
                member => new ConversionMember(
                    GetString(member, "id"),
                    GetString(member, "displayName"),
                    GetAssetUrlOrNull(member, "avatarUrl"),
                    GetStringOrNull(member, "colorHex"),
                    ParseArray(member, "roleIds", roleId => GetString(roleId))
                )
            ),
            ParseArray(
                json,
                "roles",
                role => new ConversionRole(
                    GetString(role, "id"),
                    GetString(role, "name"),
                    GetStringOrNull(role, "colorHex"),
                    GetInt32(role, "position")
                )
            ),
            ParseArray(
                json,
                "channels",
                channel => new ConversionChannel(
                    GetString(channel, "id"),
                    GetString(channel, "name"),
                    GetStringOrNull(channel, "type"),
                    GetBoolean(channel, "isVoice")
                )
            ),
            ParseArray(json, "emojis", ParseConversionEmoji)
        );
    }

    private static ConversionEmoji ParseConversionEmoji(JsonElement json) =>
        new(
            GetStringOrNull(json, "id"),
            GetString(json, "name"),
            GetBoolean(json, "isAnimated"),
            GetAssetUrl(json, "imageUrl")
        );

    private static (
        IReadOnlyList<Message> Messages,
        IReadOnlyList<ConversionEmoji> InlineEmojis,
        ConversionData? LegacyConversionData
    ) ParseMessages(JsonElement json)
    {
        var messages = new List<Message>();
        var inlineEmojis = new List<ConversionEmoji>();
        var legacyMembersById = new Dictionary<string, ConversionMember>(StringComparer.Ordinal);
        var legacyRolesById = new Dictionary<string, ConversionRole>(StringComparer.Ordinal);

        foreach (var messageJson in json.EnumerateArray())
        {
            messages.Add(ParseMessage(messageJson));
            AddInlineEmojis(messageJson, inlineEmojis);
            AddLegacyUserConversionData(messageJson, legacyMembersById, legacyRolesById);
        }

        var legacyConversionData =
            legacyMembersById.Count > 0 || legacyRolesById.Count > 0
                ? new ConversionData(
                    legacyMembersById.Values.ToArray(),
                    legacyRolesById.Values.ToArray(),
                    []
                )
                : null;

        return (
            RelinkReferencedMessages(messages),
            inlineEmojis.DistinctBy(emoji => (emoji.Id, emoji.Name, emoji.IsAnimated)).ToArray(),
            legacyConversionData
        );
    }

    private static void AddLegacyUserConversionData(
        JsonElement json,
        IDictionary<string, ConversionMember> membersById,
        IDictionary<string, ConversionRole> rolesById
    )
    {
        if (json.ValueKind != JsonValueKind.Object)
            return;

        if (json.TryGetProperty("author", out var author))
            AddLegacyUser(author, membersById, rolesById);

        foreach (var mention in EnumerateArray(json, "mentions"))
            AddLegacyUser(mention, membersById, rolesById);

        foreach (var reaction in EnumerateArray(json, "reactions"))
        {
            foreach (var user in EnumerateArray(reaction, "users"))
                AddLegacyUser(user, membersById, rolesById);
        }

        if (
            json.TryGetProperty("interaction", out var interaction)
            && interaction.ValueKind == JsonValueKind.Object
            && interaction.TryGetProperty("user", out var interactionUser)
        )
        {
            AddLegacyUser(interactionUser, membersById, rolesById);
        }

        if (json.TryGetProperty("referencedMessage", out var referencedMessage))
            AddLegacyUserConversionData(referencedMessage, membersById, rolesById);
    }

    private static void AddLegacyUser(
        JsonElement json,
        IDictionary<string, ConversionMember> membersById,
        IDictionary<string, ConversionRole> rolesById
    )
    {
        if (
            json.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(GetStringOrNull(json, "id"))
        )
            return;

        var id = GetString(json, "id");
        var roleIds = new List<string>();

        foreach (var roleJson in EnumerateArray(json, "roles"))
        {
            var roleId = GetStringOrNull(roleJson, "id");
            if (string.IsNullOrWhiteSpace(roleId))
                continue;

            roleIds.Add(roleId);
            rolesById[roleId] = new ConversionRole(
                roleId,
                GetStringOrNull(roleJson, "name") ?? roleId,
                ParseColorHex(GetStringOrNull(roleJson, "color")),
                GetInt32(roleJson, "position")
            );
        }

        var colorHex = ParseColorHex(GetStringOrNull(json, "color"));
        if (roleIds.Count <= 0 && colorHex is null)
            return;

        var member = new ConversionMember(
            id,
            GetStringOrNull(json, "nickname") ?? GetStringOrNull(json, "name") ?? id,
            GetAssetUrlOrNull(json, "avatarUrl"),
            colorHex,
            roleIds.Distinct(StringComparer.Ordinal).ToArray()
        );

        if (membersById.TryGetValue(id, out var existing))
        {
            member = member with
            {
                DisplayName = !string.IsNullOrWhiteSpace(member.DisplayName)
                    ? member.DisplayName
                    : existing.DisplayName,
                AvatarUrl = member.AvatarUrl ?? existing.AvatarUrl,
                ColorHex = member.ColorHex ?? existing.ColorHex,
                RoleIds = existing
                    .RoleIds.Concat(member.RoleIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        membersById[id] = member;
    }

    private static void AddInlineEmojis(JsonElement json, ICollection<ConversionEmoji> emojis)
    {
        foreach (var emojiJson in EnumerateArray(json, "inlineEmojis"))
        {
            var emoji = ParseConversionEmoji(emojiJson);
            if (!string.IsNullOrWhiteSpace(emoji.ImageUrl))
                emojis.Add(emoji);
        }

        if (json.TryGetProperty("referencedMessage", out var referencedMessage))
            AddInlineEmojis(referencedMessage, emojis);

        if (json.TryGetProperty("forwardedMessage", out var forwardedMessage))
            AddInlineEmojis(forwardedMessage, emojis);

        foreach (var embed in EnumerateArray(json, "embeds"))
            AddInlineEmojis(embed, emojis);
    }

    private static ConversionData? MergeConversionData(
        ConversionData? conversionData,
        ConversionData? legacyConversionData,
        IReadOnlyList<ConversionEmoji> inlineEmojis
    )
    {
        if (conversionData is null && legacyConversionData is null && inlineEmojis.Count <= 0)
            return null;

        var members = MergeByKey(
            legacyConversionData?.Members ?? [],
            conversionData?.Members ?? [],
            member => member.Id
        );
        var roles = MergeByKey(
            legacyConversionData?.Roles ?? [],
            conversionData?.Roles ?? [],
            role => role.Id
        );
        var channels = conversionData?.Channels ?? legacyConversionData?.Channels ?? [];
        var emojis = MergeByKey(
            conversionData?.Emojis ?? [],
            inlineEmojis,
            emoji => $"{emoji.Id}|{emoji.Name}|{emoji.IsAnimated}"
        );

        return new ConversionData(members, roles, channels, emojis);
    }

    private static IReadOnlyList<T> MergeByKey<T>(
        IEnumerable<T> lowPriority,
        IEnumerable<T> highPriority,
        Func<T, string> getKey
    ) =>
        lowPriority
            .Concat(highPriority)
            .GroupBy(getKey, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

    private static IReadOnlyList<Message> RelinkReferencedMessages(IReadOnlyList<Message> messages)
    {
        var messagesById = messages
            .GroupBy(message => message.Id)
            .ToDictionary(g => g.Key, g => g.First());

        return messages
            .Select(message =>
                message is { ReferencedMessage: null, Reference.MessageId: { } referencedMessageId }
                && referencedMessageId != message.Id
                && messagesById.TryGetValue(referencedMessageId, out var referencedMessage)
                    ? message with
                    {
                        ReferencedMessage = referencedMessage,
                    }
                    : message
            )
            .ToArray();
    }

    private static (Snowflake? After, Snowflake? Before) ParseDateRange(JsonElement json)
    {
        Snowflake? after = null;
        Snowflake? before = null;

        if (GetStringOrNull(json, "after") is { } afterText)
            after = Snowflake.FromDate(ParseDate(afterText));

        if (GetStringOrNull(json, "before") is { } beforeText)
            before = Snowflake.FromDate(ParseDate(beforeText));

        return (after, before);
    }

    private static IReadOnlyList<T> ParseArray<T>(
        JsonElement json,
        string propertyName,
        Func<JsonElement, T> parse
    ) => EnumerateArray(json, propertyName).Select(parse).ToArray();

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement json, string propertyName)
    {
        if (
            !json.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array
        )
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
            yield return item;
    }

    private static Snowflake ParseSnowflake(JsonElement json) => ParseSnowflake(GetString(json));

    private static Snowflake ParseSnowflake(string text) =>
        new(ulong.Parse(text, CultureInfo.InvariantCulture));

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<TEnum>(text, true, out var value)
            ? value
            : fallback;

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseDateOrNull(JsonElement json, string propertyName) =>
        GetStringOrNull(json, propertyName) is { } text ? ParseDate(text) : null;

    private static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return ColorTranslator.FromHtml(text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static string? ParseColorHex(string? text) => ParseColor(text)?.ToHexString();

    private static string GetString(JsonElement json, string propertyName) =>
        GetStringOrNull(json, propertyName) ?? "";

    private static string GetString(JsonElement json) =>
        json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : json.GetRawText();

    private static string? GetStringOrNull(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null
            ? GetString(property)
            : null;

    private static string GetAssetUrl(JsonElement json, string propertyName) =>
        GetAssetUrlOrNull(json, propertyName) ?? "";

    private static string? GetAssetUrlOrNull(JsonElement json, string propertyName) =>
        SanitizeAssetUrl(GetStringOrNull(json, propertyName));

    private static string? SanitizeAssetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmedUrl = url.Trim();
        if (trimmedUrl.StartsWith("//", StringComparison.Ordinal) || Path.IsPathRooted(trimmedUrl))
            return null;

        if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.Scheme is "http" or "https" ? trimmedUrl : null;

        if (!Uri.TryCreate(trimmedUrl, UriKind.Relative, out _))
            return null;

        foreach (var segment in trimmedUrl.Replace('\\', '/').Split('/'))
        {
            string unescapedSegment;
            try
            {
                unescapedSegment = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (unescapedSegment == "..")
                return null;
        }

        return trimmedUrl;
    }

    private static bool GetBoolean(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static int GetInt32(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property) ? property.GetInt32() : 0;

    private static int? GetInt32OrNull(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : null;

    private static long GetInt64(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property) ? property.GetInt64() : 0;
}
