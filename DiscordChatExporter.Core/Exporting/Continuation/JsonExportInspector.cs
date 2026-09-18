using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportInspector
{
    public static async ValueTask<JsonExportInfo> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        byte[] bytes;
        try
        {
            // Deliberately buffers the whole file into memory. This is acceptable because
            // inspection is an occasional, user-initiated action over a realistic personal
            // export size; we trade memory for a simple single-pass Utf8JsonReader.
            bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidJsonExportException($"Could not read '{filePath}'.", ex);
        }

        try
        {
            return Inspect(bytes);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new InvalidJsonExportException(
                "The selected file is not a valid JSON export.",
                ex
            );
        }
    }

    private static JsonExportInfo Inspect(byte[] bytes)
    {
        Snowflake? guildId = null;
        Snowflake? channelId = null;
        Snowflake? before = null;
        Snowflake? firstId = null;
        Snowflake? lastId = null;
        Snowflake? previousId = null;
        var orderDirection = 0;
        long count = 0;

        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("The selected file is not a JSON export.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read(); // advance to value

            switch (name)
            {
                case "guild":
                    guildId = ReadIdOf(ref reader, "guild");
                    break;
                case "channel":
                    channelId = ReadIdOf(ref reader, "channel");
                    break;
                case "dateRange":
                    before = ReadBeforeOf(ref reader);
                    break;
                case "messages":
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new InvalidJsonExportException("Malformed 'messages' array.");
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var (id, ts) = ReadMessageHeader(ref reader);
                        TrackMessageOrder(previousId, id, ref orderDirection);
                        firstId ??= id;
                        lastId = id;
                        previousId = id;
                        count++;
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (guildId is null || channelId is null)
            throw new InvalidJsonExportException(
                "The selected file is not a DiscordChatExporter JSON export."
            );

        if (count == 0 || lastId is null)
            throw new InvalidJsonExportException(
                "The selected export contains no messages to continue from."
            );

        var isChronological = firstId is null || firstId.Value.Value <= lastId.Value.Value;

        return new JsonExportInfo(
            guildId.Value,
            channelId.Value,
            before,
            lastId.Value,
            count,
            isChronological
        );
    }

    // Reader is positioned on the StartObject of an object that has a string "id" property.
    // Returns that id and leaves the reader on the object's EndObject. The 'label' identifies
    // which object (e.g. "guild"/"channel") so diagnostics can say what lacked an id.
    private static Snowflake ReadIdOf(ref Utf8JsonReader reader, string label)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException($"Expected the '{label}' object.");

        Snowflake? id = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else
                reader.Skip();
        }
        return id ?? throw new InvalidJsonExportException($"The '{label}' object is missing 'id'.");
    }

    private static Snowflake? ReadBeforeOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        Snowflake? before = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "before" && reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (
                    !string.IsNullOrWhiteSpace(raw)
                    && DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dto
                    )
                )
                {
                    before = Snowflake.FromDate(dto);
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return before;
    }

    // Reader positioned on the StartObject of a message. Returns id + timestamp,
    // leaves the reader on the message's EndObject.
    private static (Snowflake Id, DateTimeOffset? Timestamp) ReadMessageHeader(
        ref Utf8JsonReader reader
    )
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("Malformed message entry.");

        Snowflake? id = null;
        DateTimeOffset? ts = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else if (prop == "timestamp" && reader.TokenType == JsonTokenType.String)
                ts = reader.TryGetDateTimeOffset(out var dto) ? dto : null;
            else
                reader.Skip();
        }
        return (id ?? throw new InvalidJsonExportException("Message missing 'id'."), ts);
    }

    private static void TrackMessageOrder(
        Snowflake? previousId,
        Snowflake currentId,
        ref int orderDirection
    )
    {
        if (previousId is null)
            return;

        var comparison = currentId.Value.CompareTo(previousId.Value.Value);
        if (comparison == 0)
            throw new InvalidJsonExportException("The export contains duplicate message IDs.");

        var direction = Math.Sign(comparison);
        if (orderDirection == 0)
        {
            orderDirection = direction;
            return;
        }

        if (orderDirection != direction)
        {
            throw new InvalidJsonExportException(
                "The export's messages are not consistently ordered."
            );
        }
    }
}
