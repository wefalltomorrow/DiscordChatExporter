using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Discord.Data.Polls;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Markdown.Parsing;
using JsonExtensions.Writing;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class JsonMessageWriter(Stream stream, ExportContext context)
    : MessageWriter(stream, context)
{
    private readonly Utf8JsonWriter _writer = new(
        stream,
        new JsonWriterOptions
        {
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/450
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
            // Validation errors may mask actual failures
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/413
            SkipValidation = true,
        }
    );

    private async ValueTask WriteUserAsync(
        User user,
        bool includeRoles = true,
        CancellationToken cancellationToken = default
    )
    {
        var member = Context.TryGetMember(user.Id);
        var roles = Context.GetUserRoles(user.Id);
        var color = roles.FirstOrDefault(r => r.Color is not null)?.Color;

        _writer.WriteStartObject();

        _writer.WriteString("id", user.Id.ToString());
        _writer.WriteString("name", user.Name);
        _writer.WriteString("discriminator", user.DiscriminatorFormatted);

        _writer.WriteString("nickname", member?.DisplayName ?? user.DisplayName);

        _writer.WriteString("color", color?.ToHexString());
        _writer.WriteBoolean("isBot", user.IsBot);

        if (_isExtended)
        {
            _writer.WriteString("joinedAt", member?.JoinedAt?.Pipe(Context.NormalizeDate));
            _writer.WriteString("premiumSince", member?.PremiumSince?.Pipe(Context.NormalizeDate));
            _writer.WriteBoolean("isPending", member?.IsPending ?? false);

            // Decomposed rather than written as a bitfield, so that a reader doesn't need to know
            // the bit values. Bits Discord has added since are kept as their numeric value.
            WriteReferenceArray("flags", GetFlagNames(member?.Flags ?? MemberFlags.None));
        }

        if (includeRoles)
        {
            _writer.WritePropertyName("roles");
            await WriteRolesAsync(roles, cancellationToken);
        }

        _writer.WriteString(
            "avatarUrl",
            await Context.ResolveAssetUrlAsync(
                member?.AvatarUrl ?? user.AvatarUrl,
                cancellationToken
            )
        );

        if (_isExtended)
        {
            // Same precedence as the avatar: a guild-specific banner wins over the global one.
            // Unlike the avatar there is no fallback, so this stays null when neither is set.
            //
            // The member's own copy of the user is consulted before the one we were handed,
            // because the latter often comes from a message payload, where Discord sends only a
            // partial user object with no banner on it at all.
            var bannerUrl = member?.BannerUrl ?? member?.User.BannerUrl ?? user.BannerUrl;

            _writer.WriteString(
                "bannerUrl",
                bannerUrl is not null
                    ? await Context.ResolveAssetUrlAsync(bannerUrl, cancellationToken)
                    : null
            );
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    // A [Flags] enum's own ToString collapses to the raw number as soon as one bit is unknown,
    // which would hide the flags that *are* recognized. This keeps both.
    private static IEnumerable<string> GetFlagNames<T>(T flags)
        where T : struct, Enum
    {
        var remaining = Convert.ToInt32(flags, CultureInfo.InvariantCulture);

        foreach (var value in Enum.GetValues<T>())
        {
            var bit = Convert.ToInt32(value, CultureInfo.InvariantCulture);

            if (bit == 0 || (remaining & bit) == 0)
                continue;

            remaining &= ~bit;
            yield return value.ToString();
        }

        if (remaining != 0)
            yield return remaining.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToCamelCase(string name)
    {
        if (!name.Contains('_', StringComparison.Ordinal))
            return name;

        var buffer = new StringBuilder(name.Length);
        var shouldCapitalize = false;

        foreach (var c in name)
        {
            if (c == '_')
            {
                shouldCapitalize = true;
                continue;
            }

            buffer.Append(shouldCapitalize ? char.ToUpperInvariant(c) : c);
            shouldCapitalize = false;
        }

        return buffer.ToString();
    }

    // The component tree is mirrored rather than projected, for the reason given on the Component
    // record: only the property names are changed, from the API's snake_case to the camelCase used
    // by every other key in this document. That transform is mechanical and reversible, so the
    // result can still be read against Discord's own component documentation.
    private async ValueTask WriteComponentJsonAsync(
        JsonElement json,
        bool isMedia = false,
        CancellationToken cancellationToken = default
    )
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                _writer.WriteStartObject();

                foreach (var property in json.EnumerateObject())
                {
                    _writer.WritePropertyName(ToCamelCase(property.Name));

                    // Media referenced by a component lives on the CDN behind a signed URL that
                    // expires within the day, so it has to go through the asset pipeline like an
                    // attachment does. Only media objects are treated this way: a link button also
                    // carries a 'url', but that one points at an arbitrary site rather than at a
                    // downloadable asset.
                    if (
                        isMedia
                        && property.Value.ValueKind is JsonValueKind.String
                        && property.Name is "url" or "proxy_url"
                    )
                    {
                        _writer.WriteStringValue(
                            await Context.ResolveAssetUrlAsync(
                                property.Value.GetString() ?? "",
                                cancellationToken
                            )
                        );
                    }
                    else
                    {
                        await WriteComponentJsonAsync(
                            property.Value,
                            property.NameEquals("media") || property.NameEquals("file"),
                            cancellationToken
                        );
                    }
                }

                _writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                _writer.WriteStartArray();

                foreach (var item in json.EnumerateArray())
                    await WriteComponentJsonAsync(item, isMedia, cancellationToken);

                _writer.WriteEndArray();
                break;

            default:
                json.WriteTo(_writer);
                break;
        }
    }

    private async ValueTask WriteComponentsAsync(
        IReadOnlyList<Component> components,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartArray("components");

        foreach (var component in components)
            await WriteComponentJsonAsync(component.Json, cancellationToken: cancellationToken);

        _writer.WriteEndArray();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmojiAsync(
        Emoji emoji,
        string? key = null,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        // Only present on table entries, where it's the value that messages reference
        if (key is not null)
            _writer.WriteString("key", key);

        _writer.WriteString("id", emoji.Id.ToString());
        _writer.WriteString("name", emoji.Name);
        _writer.WriteString("code", emoji.Code);
        _writer.WriteBoolean("isAnimated", emoji.IsAnimated);
        _writer.WriteString(
            "imageUrl",
            await Context.ResolveAssetUrlAsync(emoji.ImageUrl, cancellationToken)
        );

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteRolesAsync(
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartArray();

        foreach (var role in roles)
        {
            _writer.WriteStartObject();

            _writer.WriteString("id", role.Id.ToString());
            _writer.WriteString("name", role.Name);
            _writer.WriteString("color", role.Color?.ToHexString());
            _writer.WriteNumber("position", role.Position);

            _writer.WriteEndObject();
        }

        _writer.WriteEndArray();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteAttachmentAsync(
        Attachment attachment,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", attachment.Id.ToString());
        _writer.WriteString(
            "url",
            await Context.ResolveAssetUrlAsync(attachment.Url, cancellationToken)
        );
        _writer.WriteString("fileName", attachment.FileName);
        _writer.WriteString("description", attachment.Description);
        if (attachment.Width is { } width)
            _writer.WriteNumber("width", width);
        else
            _writer.WriteNull("width");

        if (attachment.Height is { } height)
            _writer.WriteNumber("height", height);
        else
            _writer.WriteNull("height");

        _writer.WriteNumber("fileSizeBytes", attachment.FileSize.TotalBytes);

        _writer.WriteEndObject();
    }

    private async ValueTask WriteEmbedAuthorAsync(
        EmbedAuthor embedAuthor,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("name", embedAuthor.Name);
        _writer.WriteString("url", embedAuthor.Url);

        if (!string.IsNullOrWhiteSpace(embedAuthor.IconUrl))
        {
            _writer.WriteString(
                "iconUrl",
                await Context.ResolveAssetUrlAsync(
                    embedAuthor.IconProxyUrl ?? embedAuthor.IconUrl,
                    cancellationToken
                )
            );

            _writer.WriteString("iconCanonicalUrl", embedAuthor.IconUrl);
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedImageAsync(
        EmbedImage embedImage,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        if (!string.IsNullOrWhiteSpace(embedImage.Url))
        {
            _writer.WriteString(
                "url",
                await Context.ResolveAssetUrlAsync(
                    embedImage.ProxyUrl ?? embedImage.Url,
                    cancellationToken
                )
            );

            _writer.WriteString("canonicalUrl", embedImage.Url);
        }

        _writer.WriteNumber("width", embedImage.Width);
        _writer.WriteNumber("height", embedImage.Height);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedVideoAsync(
        EmbedVideo embedVideo,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        if (!string.IsNullOrWhiteSpace(embedVideo.Url))
        {
            _writer.WriteString(
                "url",
                await Context.ResolveAssetUrlAsync(
                    embedVideo.ProxyUrl ?? embedVideo.Url,
                    cancellationToken
                )
            );

            _writer.WriteString("canonicalUrl", embedVideo.Url);
        }

        _writer.WriteNumber("width", embedVideo.Width);
        _writer.WriteNumber("height", embedVideo.Height);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedFooterAsync(
        EmbedFooter embedFooter,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("text", embedFooter.Text);

        if (!string.IsNullOrWhiteSpace(embedFooter.IconUrl))
        {
            _writer.WriteString(
                "iconUrl",
                await Context.ResolveAssetUrlAsync(
                    embedFooter.IconProxyUrl ?? embedFooter.IconUrl,
                    cancellationToken
                )
            );

            _writer.WriteString("iconCanonicalUrl", embedFooter.IconUrl);
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedFieldAsync(
        EmbedField embedField,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("name", await FormatMarkdownAsync(embedField.Name, cancellationToken));
        _writer.WriteString("nameRaw", embedField.Name);
        _writer.WriteString(
            "value",
            await FormatMarkdownAsync(embedField.Value, cancellationToken)
        );
        _writer.WriteString("valueRaw", embedField.Value);
        _writer.WriteBoolean("isInline", embedField.IsInline);

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteEmbedAsync(
        Embed embed,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString(
            "title",
            await FormatMarkdownAsync(embed.Title ?? "", cancellationToken)
        );
        _writer.WriteString("titleRaw", embed.Title);
        _writer.WriteString("type", embed.Kind.ToString());
        _writer.WriteString("url", embed.Url);
        _writer.WriteString("timestamp", embed.Timestamp?.Pipe(Context.NormalizeDate));
        _writer.WriteString(
            "description",
            await FormatMarkdownAsync(embed.Description ?? "", cancellationToken)
        );
        _writer.WriteString("descriptionRaw", embed.Description);

        if (embed.Color is not null)
            _writer.WriteString("color", embed.Color.Value.ToHexString());

        if (embed.Author is not null)
        {
            _writer.WritePropertyName("author");
            await WriteEmbedAuthorAsync(embed.Author, cancellationToken);
        }

        if (embed.Thumbnail is not null)
        {
            _writer.WritePropertyName("thumbnail");
            await WriteEmbedImageAsync(embed.Thumbnail, cancellationToken);
        }

        if (embed.Image is not null)
        {
            _writer.WritePropertyName("image");
            await WriteEmbedImageAsync(embed.Image, cancellationToken);
        }

        if (embed.Video is not null)
        {
            _writer.WritePropertyName("video");
            await WriteEmbedVideoAsync(embed.Video, cancellationToken);
        }

        if (embed.Footer is not null)
        {
            _writer.WritePropertyName("footer");
            await WriteEmbedFooterAsync(embed.Footer, cancellationToken);
        }

        // Images
        _writer.WriteStartArray("images");

        foreach (var image in embed.Images)
            await WriteEmbedImageAsync(image, cancellationToken);

        _writer.WriteEndArray();

        // Fields
        _writer.WriteStartArray("fields");

        foreach (var field in embed.Fields)
            await WriteEmbedFieldAsync(field, cancellationToken);

        _writer.WriteEndArray();

        // Inline emoji
        IEnumerable<Emoji> inlineEmojis = !string.IsNullOrWhiteSpace(embed.Description)
            ? MarkdownParser
                .ExtractEmojis(embed.Description)
                .DistinctBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new Emoji(e.Id, e.Name, e.IsAnimated))
            : [];

        if (_isNormalized)
        {
            foreach (
                var emoji in MarkdownParser
                    .ExtractEmojis(embed.Description)
                    .DistinctBy(e => (e.Id, e.Name, e.IsAnimated))
            )
            {
                await WriteEmojiAsync(
                    new Emoji(emoji.Id, emoji.Name, emoji.IsAnimated),
                    cancellationToken
                );
            }
        }
        else
        {
            _writer.WriteStartArray("inlineEmojis");

            foreach (var emoji in inlineEmojis)
                await WriteEmojiAsync(emoji, cancellationToken: cancellationToken);

            _writer.WriteEndArray();
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WritePollAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        _writer.WriteStartObject();

        _writer.WriteString("question", poll.Question);
        _writer.WriteString("expiresAt", poll.ExpiresAt?.Pipe(Context.NormalizeDate));
        _writer.WriteBoolean("allowsMultipleAnswers", poll.AllowsMultipleAnswers);

        _writer.WriteStartArray("answers");
        foreach (var answer in poll.Answers)
        {
            _writer.WriteStartObject();
            _writer.WriteNumber("id", answer.Id);
            _writer.WriteString("text", answer.Text);

            if (answer.Emoji is not null)
            {
                _writer.WritePropertyName("emoji");
                await WriteEmojiAsync(answer.Emoji, cancellationToken);
            }

            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        if (poll.Results is not null)
        {
            _writer.WriteStartObject("results");
            _writer.WriteBoolean("isFinalized", poll.Results.IsFinalized);

            _writer.WriteStartArray("answers");
            foreach (var answerResult in poll.Results.Answers)
            {
                _writer.WriteStartObject();
                _writer.WriteNumber("id", answerResult.Id);
                _writer.WriteNumber("count", answerResult.Count);
                _writer.WriteBoolean("didCurrentUserVote", answerResult.DidCurrentUserVote);
                _writer.WriteEndObject();
            }
            _writer.WriteEndArray();

            _writer.WriteEndObject();
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteStickerAsync(
        Sticker sticker,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject();

        _writer.WriteString("id", sticker.Id.ToString());
        _writer.WriteString("name", sticker.Name);
        _writer.WriteString("format", sticker.Format.ToString());
        _writer.WriteString(
            "sourceUrl",
            await Context.ResolveAssetUrlAsync(sticker.SourceUrl, cancellationToken)
        );

        _writer.WriteEndObject();
    }

    private async ValueTask WriteConversionDataAsync(CancellationToken cancellationToken = default)
    {
        _writer.WriteStartObject("conversionData");
        _writer.WriteNumber("schemaVersion", ConversionData.CurrentSchemaVersion);

        _writer.WriteStartArray("members");
        foreach (var (id, member) in Context.CachedMembers.OrderBy(m => m.Key.Value))
        {
            if (member is null)
                continue;

            _writer.WriteStartObject();
            _writer.WriteString("id", id.ToString());
            _writer.WriteString("displayName", member.DisplayName ?? member.User.DisplayName);
            _writer.WriteString("avatarUrl", member.AvatarUrl ?? member.User.AvatarUrl);
            _writer.WriteString("colorHex", Context.TryGetUserColor(id)?.ToHexString());
            _writer.WriteStartArray("roleIds");
            foreach (var roleId in member.RoleIds)
                _writer.WriteStringValue(roleId.ToString());
            _writer.WriteEndArray();
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        _writer.WriteStartArray("roles");
        foreach (var (_, role) in Context.CachedRoles.OrderBy(r => r.Key.Value))
        {
            _writer.WriteStartObject();
            _writer.WriteString("id", role.Id.ToString());
            _writer.WriteString("name", role.Name);
            _writer.WriteString("colorHex", role.Color?.ToHexString());
            _writer.WriteNumber("position", role.Position);
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        _writer.WriteStartArray("channels");
        foreach (var (id, channel) in Context.CachedChannels.OrderBy(c => c.Key.Value))
        {
            if (channel is null)
                continue;

            _writer.WriteStartObject();
            _writer.WriteString("id", id.ToString());
            _writer.WriteString("name", channel.Name);
            _writer.WriteString("type", channel.Kind.ToString());
            _writer.WriteBoolean("isVoice", channel.IsVoice);
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask WritePreambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Root object (start)
        _writer.WriteStartObject();

        // Modifications made by this fork, so that parsers can detect them up-front
        _writer.WriteStartObject("mod");
        _writer.WriteBoolean("normal", _isNormalized);
        _writer.WriteBoolean("extended", _isExtended);
        _writer.WriteBoolean("reactionUsers", _shouldFetchReactionUsers);
        // Provenance: member data in this export may be up to the cache TTL old
        _writer.WriteBoolean("cache", Context.Request.IsCacheEnabled);
        _writer.WriteEndObject();

        // Guild
        var guild = Context.Request.Guild;

        _writer.WriteStartObject("guild");
        _writer.WriteString("id", guild.Id.ToString());
        _writer.WriteString("name", guild.Name);

        _writer.WriteString(
            "iconUrl",
            await Context.ResolveAssetUrlAsync(guild.IconUrl, cancellationToken)
        );

        if (_isExtended)
            await WriteGuildExtrasAsync(guild, cancellationToken);

        _writer.WriteEndObject();

        // Channel
        _writer.WriteStartObject("channel");
        _writer.WriteString("id", Context.Request.Channel.Id.ToString());
        _writer.WriteString("type", Context.Request.Channel.Kind.ToString());

        // Original schema did not account for threads, so 'category' actually refers to the parent channel
        _writer.WriteString("categoryId", Context.Request.Channel.Parent?.Id.ToString());
        _writer.WriteString("category", Context.Request.Channel.Parent?.Name);

        _writer.WriteString("name", Context.Request.Channel.Name);
        _writer.WriteString("topic", Context.Request.Channel.Topic);

        if (!string.IsNullOrWhiteSpace(Context.Request.Channel.IconUrl))
        {
            _writer.WriteString(
                "iconUrl",
                await Context.ResolveAssetUrlAsync(
                    Context.Request.Channel.IconUrl,
                    cancellationToken
                )
            );
        }

        if (_isExtended)
            await WriteChannelExtrasAsync(Context.Request.Channel, cancellationToken);

        _writer.WriteEndObject();

        // Date range
        _writer.WriteStartObject("dateRange");
        _writer.WriteString("after", Context.Request.After?.ToDate().Pipe(Context.NormalizeDate));
        _writer.WriteString("before", Context.Request.Before?.ToDate().Pipe(Context.NormalizeDate));
        _writer.WriteEndObject();

        // Timestamp
        _writer.WriteString("exportedAt", Context.NormalizeDate(DateTimeOffset.UtcNow));

        // Message array (start)
        _writer.WriteStartArray("messages");
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        await base.WriteMessageAsync(message, cancellationToken);

        _writer.WriteStartObject();

        // Metadata
        _writer.WriteString("id", message.Id.ToString());
        _writer.WriteString("type", message.Kind.ToString());
        _writer.WriteNumber("flags", (int)message.Flags);
        _writer.WriteString("timestamp", Context.NormalizeDate(message.Timestamp));
        _writer.WriteString(
            "timestampEdited",
            message.EditedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteString(
            "callEndedTimestamp",
            message.CallEndedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteBoolean("isPinned", message.IsPinned);

        if (_isExtended)
            WriteReferenceArray("flags", GetFlagNames(message.Flags));

        // Content
        if (message.IsSystemNotification)
        {
            _writer.WriteString("content", message.GetFallbackContent());
            _writer.WriteString("contentRaw", message.Content);
        }
        else
        {
            _writer.WriteString(
                "content",
                await FormatMarkdownAsync(message.Content, cancellationToken)
            );
            _writer.WriteString("contentRaw", message.Content);
        }

        // Author
        if (_isNormalized)
        {
            _writer.WriteString("authorId", RegisterUser(message.Author));
        }
        else
        {
            _writer.WritePropertyName("author");
            await WriteUserAsync(message.Author, true, cancellationToken);
        }

        // Attachments
        // Not normalized: an attachment belongs to exactly one message, so it never repeats
        _writer.WriteStartArray("attachments");

        foreach (var attachment in message.Attachments)
            await WriteAttachmentAsync(attachment, cancellationToken);

        _writer.WriteEndArray();

        // Embeds
        _writer.WriteStartArray("embeds");

        foreach (var embed in message.Embeds)
            await WriteEmbedAsync(embed, cancellationToken);

        _writer.WriteEndArray();

        // Stickers
        await WriteStickersAsync(message.Stickers, cancellationToken);

        // Components
        if (_isExtended)
            await WriteComponentsAsync(message.Components, cancellationToken);

        // Reactions
        _writer.WriteStartArray("reactions");

        foreach (var reaction in message.Reactions)
        {
            _writer.WriteStartObject();

            // Emoji
            if (_isNormalized)
            {
                _writer.WriteString("emojiKey", RegisterEmoji(reaction.Emoji));
            }
            else
            {
                _writer.WritePropertyName("emoji");
                await WriteEmojiAsync(reaction.Emoji, cancellationToken: cancellationToken);
            }

            _writer.WriteNumber("count", reaction.Count);

            // Reaction authors
            if (_isNormalized)
                _writer.WriteStartArray("userIds");
            else
                _writer.WriteStartArray("users");

            // Fetching the reacting users costs a request per 100 of them, per reaction, which
            // is the single most expensive thing this exporter does. The emoji and the count come
            // free with the message itself, so they are written either way; only the list is
            // skipped. It stays an empty array rather than being omitted, so that the schema is
            // the same in both cases, with 'mod.reactionUsers' recording which one this is.
            if (_shouldFetchReactionUsers)
            {
                await foreach (
                    var user in Context.Discord.GetMessageReactionsAsync(
                        Context.Request.Channel.Id,
                        message.Id,
                        reaction.Emoji,
                        cancellationToken
                    )
                )
                {
                    if (_isNormalized)
                        _writer.WriteStringValue(RegisterUser(user));
                    else
                        await WriteUserAsync(user, false, cancellationToken);
                }
            }

            _writer.WriteEndArray();

            _writer.WriteEndObject();
        }

        _writer.WriteEndArray();

        // Mentions
        if (_isNormalized)
        {
            WriteReferenceArray("mentionIds", message.MentionedUsers.Select(RegisterUser));
        }
        else
        {
            _writer.WriteStartArray("mentions");

            foreach (var user in message.MentionedUsers)
                await WriteUserAsync(user, true, cancellationToken);

            _writer.WriteEndArray();
        }

        // Message reference
        if (message.Reference is not null)
        {
            _writer.WriteStartObject("reference");
            _writer.WriteString("type", message.Reference.Kind.ToString());
            _writer.WriteString("messageId", message.Reference.MessageId?.ToString());
            _writer.WriteString("channelId", message.Reference.ChannelId?.ToString());
            _writer.WriteString("guildId", message.Reference.GuildId?.ToString());
            _writer.WriteEndObject();
        }

        // Referenced message
        if (message.ReferencedMessage is not null)
            await WriteReferencedMessageAsync(message.ReferencedMessage, cancellationToken);

        // Forwarded message
        if (message.ForwardedMessage is not null)
        {
            _writer.WriteStartObject("forwardedMessage");

            _writer.WriteString(
                "timestamp",
                Context.NormalizeDate(message.ForwardedMessage.Timestamp)
            );

            _writer.WriteString(
                "timestampEdited",
                message.ForwardedMessage.EditedTimestamp?.Pipe(Context.NormalizeDate)
            );

            _writer.WriteString(
                "content",
                await FormatMarkdownAsync(message.ForwardedMessage.Content, cancellationToken)
            );
            _writer.WriteString("contentRaw", message.ForwardedMessage.Content);

            // Forwarded attachments
            _writer.WriteStartArray("attachments");

            foreach (var attachment in message.ForwardedMessage.Attachments)
                await WriteAttachmentAsync(attachment, cancellationToken);

            _writer.WriteEndArray();

            // Forwarded embeds
            _writer.WriteStartArray("embeds");
            foreach (var embed in message.ForwardedMessage.Embeds)
                await WriteEmbedAsync(embed, cancellationToken);
            _writer.WriteEndArray();

            // Forwarded stickers
            await WriteStickersAsync(message.ForwardedMessage.Stickers, cancellationToken);

            // Forwarded components
            if (_isExtended)
                await WriteComponentsAsync(message.ForwardedMessage.Components, cancellationToken);

            _writer.WriteEndArray();

            _writer.WriteStartArray("inlineEmojis");
            foreach (
                var emoji in MarkdownParser
                    .ExtractEmojis(message.ForwardedMessage.Content)
                    .DistinctBy(e => (e.Id, e.Name, e.IsAnimated))
            )
            {
                await WriteEmojiAsync(
                    new Emoji(emoji.Id, emoji.Name, emoji.IsAnimated),
                    cancellationToken
                );
            }
            _writer.WriteEndArray();

            _writer.WriteEndObject();
        }

        // Interaction
        if (message.Interaction is not null)
        {
            _writer.WriteStartObject("interaction");

            _writer.WriteString("id", message.Interaction.Id.ToString());
            _writer.WriteString("name", message.Interaction.Name);

            if (_isNormalized)
            {
                _writer.WriteString("userId", RegisterUser(message.Interaction.User));
            }
            else
            {
                _writer.WritePropertyName("user");
                await WriteUserAsync(message.Interaction.User, true, cancellationToken);
            }

            _writer.WriteEndObject();
        }

        // Poll
        if (message.Poll is not null)
        {
            _writer.WritePropertyName("poll");
            await WritePollAsync(message.Poll, cancellationToken);
        }

        // Inline emoji
        var inlineEmojis = MarkdownParser
            .ExtractEmojis(message.Content)
            .DistinctBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new Emoji(e.Id, e.Name, e.IsAnimated));

        foreach (
            var emoji in MarkdownParser
                .ExtractEmojis(message.Content)
                .DistinctBy(e => (e.Id, e.Name, e.IsAnimated))
        )
        {
            WriteReferenceArray("inlineEmojiKeys", inlineEmojis.Select(RegisterEmoji));
        }
        else
        {
            _writer.WriteStartArray("inlineEmojis");

            foreach (var emoji in inlineEmojis)
                await WriteEmojiAsync(emoji, cancellationToken: cancellationToken);

            _writer.WriteEndArray();
        }

        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteReferencedMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        _writer.WriteStartObject("referencedMessage");

        _writer.WriteString("id", message.Id.ToString());
        _writer.WriteString("type", message.Kind.ToString());
        _writer.WriteNumber("flags", (int)message.Flags);
        _writer.WriteString("timestamp", Context.NormalizeDate(message.Timestamp));
        _writer.WriteString(
            "timestampEdited",
            message.EditedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteString(
            "callEndedTimestamp",
            message.CallEndedTimestamp?.Pipe(Context.NormalizeDate)
        );
        _writer.WriteBoolean("isPinned", message.IsPinned);
        _writer.WriteString(
            "content",
            await FormatMarkdownAsync(message.Content, cancellationToken)
        );
        _writer.WriteString("contentRaw", message.Content);

        _writer.WritePropertyName("author");
        await WriteUserAsync(message.Author, true, cancellationToken);

        _writer.WriteStartArray("attachments");
        foreach (var attachment in message.Attachments)
            await WriteAttachmentAsync(attachment, cancellationToken);
        _writer.WriteEndArray();

        _writer.WriteStartArray("embeds");
        foreach (var embed in message.Embeds)
            await WriteEmbedAsync(embed, cancellationToken);
        _writer.WriteEndArray();

        _writer.WriteStartArray("stickers");
        foreach (var sticker in message.Stickers)
            await WriteStickerAsync(sticker, cancellationToken);
        _writer.WriteEndArray();

        _writer.WriteStartArray("reactions");
        foreach (var reaction in message.Reactions)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("emoji");
            await WriteEmojiAsync(reaction.Emoji, cancellationToken);
            _writer.WriteNumber("count", reaction.Count);
            _writer.WriteStartArray("users");
            _writer.WriteEndArray();
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();

        _writer.WriteStartArray("mentions");
        foreach (var user in message.MentionedUsers)
            await WriteUserAsync(user, true, cancellationToken);
        _writer.WriteEndArray();

        if (message.Poll is not null)
        {
            _writer.WritePropertyName("poll");
            await WritePollAsync(message.Poll, cancellationToken);
        }

        _writer.WriteStartArray("inlineEmojis");
        foreach (
            var emoji in MarkdownParser
                .ExtractEmojis(message.Content)
                .DistinctBy(e => (e.Id, e.Name, e.IsAnimated))
        )
        {
            await WriteEmojiAsync(
                new Emoji(emoji.Id, emoji.Name, emoji.IsAnimated),
                cancellationToken
            );
        }
        _writer.WriteEndArray();

        _writer.WriteEndObject();
    }

    public override async ValueTask WritePostambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Message array (end)
        _writer.WriteEndArray();

        if (_isNormalized)
            await WriteLookupTablesAsync(cancellationToken);

        _writer.WriteNumber("messageCount", MessagesWritten);

        await WriteConversionDataAsync(cancellationToken);

        // Root object (end)
        _writer.WriteEndObject();
        await _writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await base.DisposeAsync();
    }
}
