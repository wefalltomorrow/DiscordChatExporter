using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Reads one message transcript JSON from an extracted Discord Data Package.
// A Data Package includes only messages authored by the account that requested it.
internal static class DiscordDataPackageReader
{
    private sealed record ChannelMetadata(
        Snowflake? ChannelId,
        Snowflake? GuildId,
        string? ChannelName,
        string? GuildName,
        int? ChannelType
    );

    public static async ValueTask<ParsedExport> ParseAsync(
        string filePath,
        JsonElement root,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetTranscriptMessages(filePath, root, out var messagesJson))
        {
            throw new InvalidExportException(
                $"'{filePath}' is neither a DiscordChatExporter JSON export nor a Discord Data Package message transcript."
            );
        }

        var metadata = await ReadChannelMetadataAsync(filePath, cancellationToken);
        var channelId = metadata.ChannelId ?? GetChannelIdFromDirectory(filePath);
        if (channelId is null)
            throw new InvalidExportException("Could not determine the transcript's channel ID.");

        var channelName = metadata.ChannelName;
        if (string.IsNullOrWhiteSpace(channelName))
            channelName = await FindMappedChannelNameAsync(
                filePath,
                channelId.Value,
                cancellationToken
            );
        channelName = string.IsNullOrWhiteSpace(channelName)
            ? channelId.Value.ToString()
            : channelName;

        var isGuildChannel = metadata.GuildId is not null;
        var guild = isGuildChannel
            ? new Guild(
                metadata.GuildId!.Value,
                string.IsNullOrWhiteSpace(metadata.GuildName)
                    ? $"Server {metadata.GuildId.Value}"
                    : metadata.GuildName,
                ""
            )
            : Guild.DirectMessages;
        var channelKind =
            isGuildChannel ? ChannelKind.GuildTextChat
            : metadata.ChannelType == (int)ChannelKind.DirectGroupTextChat
                ? ChannelKind.DirectGroupTextChat
            : ChannelKind.DirectTextChat;
        var messages = ParseMessages(messagesJson, cancellationToken);
        var channel = new Channel(
            channelId.Value,
            channelKind,
            guild.Id,
            null,
            channelName,
            null,
            null,
            null,
            false,
            messages.LastOrDefault()?.Id
        );

        return new ParsedExport(guild, channel, messages, null, false, null, null);
    }

    private static bool TryGetTranscriptMessages(
        string filePath,
        JsonElement root,
        out JsonElement messages
    )
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            var property = FindProperty(root, "messages", "transcript");
            if (property is { ValueKind: JsonValueKind.Array } array && IsMessageArray(array))
            {
                messages = array;
                return true;
            }
        }

        if (root.ValueKind == JsonValueKind.Array && IsMessageArray(root))
        {
            messages = root;
            return true;
        }

        // Empty Data Package transcripts cannot be identified from their rows, so use the
        // package's messages.json name and either its c<ID> folder or channel.json sibling.
        if (
            root.ValueKind == JsonValueKind.Array
            && root.GetArrayLength() == 0
            && Path.GetFileName(filePath)
                .Equals("messages.json", StringComparison.OrdinalIgnoreCase)
            && (
                GetChannelIdFromDirectory(filePath) is not null
                || HasChannelMetadataSibling(filePath)
            )
        )
        {
            messages = root;
            return true;
        }

        messages = default;
        return false;
    }

    private static bool IsMessageArray(JsonElement json) =>
        json.ValueKind == JsonValueKind.Array
        && json.EnumerateArray()
            .All(message =>
                message.ValueKind == JsonValueKind.Object
                && FindProperty(message, "id") is not null
                && FindProperty(message, "timestamp") is not null
                && FindProperty(message, "contents", "content") is not null
            );

    private static IReadOnlyList<Message> ParseMessages(
        JsonElement json,
        CancellationToken cancellationToken
    )
    {
        var author = new User(Snowflake.Zero, false, null, "You", "You", "");
        var messages = new List<Message>();

        foreach (var messageJson in json.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Snowflake.Parse(GetString(FindProperty(messageJson, "id")));
            var timestamp = ParseTimestamp(GetString(FindProperty(messageJson, "timestamp")));
            var content = GetString(
                FindProperty(messageJson, "contents", "content"),
                allowNull: true
            );
            var attachments = ParseAttachments(FindProperty(messageJson, "attachments"));

            messages.Add(
                new Message(
                    id,
                    MessageKind.Default,
                    MessageFlags.None,
                    author,
                    timestamp,
                    null,
                    null,
                    false,
                    content,
                    attachments,
                    [],
                    [],
                    [],
                    [],
                    null,
                    null,
                    null,
                    null
                )
            );
        }

        return messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.Id)
            .ToArray();
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        // Package timestamps are UTC. Assume UTC explicitly for older values without an offset,
        // so conversion does not depend on the machine's local time zone.
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
        );
    }

    private static IReadOnlyList<Attachment> ParseAttachments(JsonElement? json)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        var attachments = new List<Attachment>();
        var values =
            json.Value.ValueKind == JsonValueKind.Array
                ? json.Value.EnumerateArray().ToArray()
                : [json.Value];

        foreach (var value in values)
        {
            string[] urls =
                value.ValueKind == JsonValueKind.String
                    ? (value.GetString() ?? "").Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries
                    )
                    : [GetString(FindProperty(value, "url", "cdnlink", "link"), allowNull: true)];

            foreach (var url in urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;
                if (uri.Scheme is not ("http" or "https"))
                    continue;

                var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                attachments.Add(
                    new Attachment(
                        Snowflake.Zero,
                        uri.ToString(),
                        string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName,
                        null,
                        null,
                        null,
                        FileSize.FromBytes(0)
                    )
                );
            }
        }

        return attachments;
    }

    private static bool HasChannelMetadataSibling(string transcriptPath)
    {
        var directory = Path.GetDirectoryName(transcriptPath);
        return !string.IsNullOrWhiteSpace(directory)
            && Directory.Exists(directory)
            && Directory
                .EnumerateFiles(directory)
                .Any(path =>
                    Path.GetFileName(path)
                        .Equals("channel.json", StringComparison.OrdinalIgnoreCase)
                );
    }

    private static async ValueTask<ChannelMetadata> ReadChannelMetadataAsync(
        string transcriptPath,
        CancellationToken cancellationToken
    )
    {
        var directory = Path.GetDirectoryName(transcriptPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new ChannelMetadata(null, null, null, null, null);

        var channelMetadataPath = Directory
            .EnumerateFiles(directory)
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals("channel.json", StringComparison.OrdinalIgnoreCase)
            );
        if (channelMetadataPath is null)
            return new ChannelMetadata(null, null, null, null, null);

        await using var stream = File.OpenRead(channelMetadataPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        return ParseMetadata(document.RootElement);
    }

    private static ChannelMetadata ParseMetadata(JsonElement json)
    {
        var guildJson = FindProperty(json, "guild");
        var guildId = ParseOptionalSnowflake(
            FindProperty(json, "guildid", "guild_id")
                ?? (
                    guildJson is { ValueKind: JsonValueKind.Object }
                        ? FindProperty(guildJson.Value, "id")
                        : null
                )
        );
        var guildName = GetString(
            FindProperty(json, "guildname", "guild_name")
                ?? (
                    guildJson is { ValueKind: JsonValueKind.Object }
                        ? FindProperty(guildJson.Value, "name")
                        : null
                ),
            allowNull: true
        );
        var channelId = ParseOptionalSnowflake(FindProperty(json, "channelid", "channel_id", "id"));
        var channelName = GetString(
            FindProperty(json, "channelname", "channel_name", "name"),
            allowNull: true
        );
        var channelTypeValue = FindProperty(json, "type", "channeltype", "channel_type");
        int? channelType = channelTypeValue is null
            ? null
            : int.Parse(GetString(channelTypeValue), CultureInfo.InvariantCulture);

        return new ChannelMetadata(channelId, guildId, channelName, guildName, channelType);
    }

    private static Snowflake? GetChannelIdFromDirectory(string filePath)
    {
        var directoryName = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
        if (directoryName.StartsWith("c", StringComparison.OrdinalIgnoreCase))
            directoryName = directoryName[1..];

        return ulong.TryParse(
            directoryName,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? new Snowflake(value)
            : null;
    }

    private static async ValueTask<string?> FindMappedChannelNameAsync(
        string transcriptPath,
        Snowflake channelId,
        CancellationToken cancellationToken
    )
    {
        var messagesDirectory = Directory
            .GetParent(Path.GetDirectoryName(transcriptPath) ?? "")
            ?.FullName;
        if (string.IsNullOrWhiteSpace(messagesDirectory) || !Directory.Exists(messagesDirectory))
            return null;

        var indexPath = Directory
            .EnumerateFiles(messagesDirectory)
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals("index.json", StringComparison.OrdinalIgnoreCase)
            );
        if (indexPath is null)
            return null;

        await using var stream = File.OpenRead(indexPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        return TryFindMappedChannelName(document.RootElement, channelId.ToString(), out var name)
            ? name
            : null;
    }

    private static bool TryFindMappedChannelName(
        JsonElement json,
        string channelId,
        out string? name
    )
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (string.Equals(property.Name, channelId, StringComparison.Ordinal))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        name = property.Value.GetString();
                        return !string.IsNullOrWhiteSpace(name);
                    }

                    var nestedName = GetString(
                        FindProperty(property.Value, "channelname", "channel_name", "name"),
                        allowNull: true
                    );
                    if (!string.IsNullOrWhiteSpace(nestedName))
                    {
                        name = nestedName;
                        return true;
                    }
                }

                if (TryFindMappedChannelName(property.Value, channelId, out name))
                    return true;
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray())
            {
                if (TryFindMappedChannelName(item, channelId, out name))
                    return true;
            }
        }

        name = null;
        return false;
    }

    private static JsonElement? FindProperty(JsonElement json, params string[] names)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in json.EnumerateObject())
        {
            var normalizedName = Normalize(property.Name);
            if (names.Any(name => normalizedName == Normalize(name)))
                return property.Value;
        }

        return null;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string GetString(JsonElement? json, bool allowNull = false)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (allowNull)
                return "";
            throw new InvalidExportException("A required value is missing from the transcript.");
        }

        return json.Value.ValueKind switch
        {
            JsonValueKind.String => json.Value.GetString() ?? "",
            JsonValueKind.Number => json.Value.GetRawText(),
            _ when allowNull => "",
            _ => throw new InvalidExportException("A transcript value has an unsupported type."),
        };
    }

    private static Snowflake? ParseOptionalSnowflake(JsonElement? json)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var value = GetString(json, allowNull: true);
        return string.IsNullOrWhiteSpace(value) ? null : Snowflake.Parse(value);
    }
}
