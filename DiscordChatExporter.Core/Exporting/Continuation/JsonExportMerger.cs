using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportMerger
{
    // Merges the 'messages' from newMessagesFilePath into existingFilePath, preserving the
    // existing preamble (guild/channel/dateRange), refreshing exportedAt, and recomputing
    // messageCount. Writes to a temp file then atomically replaces the original (with .bak).
    // Returns the merged total message count.
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    )
    {
        var tempPath = AtomicFile.CreateSiblingTempPath(existingFilePath, ".merging.tmp");
        long total;

        try
        {
            {
                await using var existingStream = File.OpenRead(existingFilePath);
                await using var newStream = File.OpenRead(newMessagesFilePath);
                using var existingDocument = await JsonDocument.ParseAsync(
                    existingStream,
                    cancellationToken: cancellationToken
                );
                using var newDocument = await JsonDocument.ParseAsync(
                    newStream,
                    cancellationToken: cancellationToken
                );

                await using var outStream = File.Create(tempPath);
                await using var writer = new Utf8JsonWriter(
                    outStream,
                    new JsonWriterOptions
                    {
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        Indented = true,
                        SkipValidation = true,
                    }
                );

                total = Merge(
                    existingDocument.RootElement,
                    newDocument.RootElement,
                    exportedAt,
                    writer,
                    cancellationToken
                );
                await writer.FlushAsync(cancellationToken);
            }

            AtomicFile.ReplaceWithBackupCleanup(tempPath, existingFilePath);
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            { /* best-effort temp cleanup */
            }

            if (ex is OperationCanceledException)
                throw;

            if (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidExportException(
                    $"Could not continue the JSON export '{existingFilePath}'. "
                        + "The file may be locked or not a valid DiscordChatExporter JSON export.",
                    ex
                );
            }

            throw;
        }

        return total;
    }

    private static long Merge(
        JsonElement existingRoot,
        JsonElement newRoot,
        DateTimeOffset exportedAt,
        Utf8JsonWriter writer,
        CancellationToken cancellationToken
    )
    {
        long total = 0;
        var wroteConversionData = false;
        var wroteMessages = false;

        if (existingRoot.ValueKind != JsonValueKind.Object)
            throw new InvalidExportException(
                "The existing JSON export is not a top-level JSON object."
            );

        var existingConversionData = GetConversionData(existingRoot);
        var newConversionData = GetConversionData(newRoot);
        var hasMergedConversionData = HasConversionData(existingConversionData, newConversionData);
        writer.WriteStartObject();

        foreach (var property in existingRoot.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (property.Name)
            {
                case "exportedAt":
                    writer.WriteString("exportedAt", exportedAt);
                    break;

                case "messageCount":
                    break; // drop; recomputed below

                case "messages":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new InvalidExportException(
                            "The existing JSON export has a malformed 'messages' property."
                        );

                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    foreach (var message in property.Value.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        message.WriteTo(writer);
                        total++;
                    }
                    total += AppendNewMessages(newRoot, writer, cancellationToken);
                    writer.WriteEndArray();
                    wroteMessages = true;
                    break;

                case "conversionData":
                    if (hasMergedConversionData)
                    {
                        WriteMergedConversionData(
                            existingConversionData,
                            newConversionData,
                            writer
                        );
                        wroteConversionData = true;
                    }
                    break;

                default:
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                    break;
            }
        }

        if (hasMergedConversionData && !wroteConversionData)
            WriteMergedConversionData(existingConversionData, newConversionData, writer);

        if (!wroteMessages)
            throw new InvalidExportException(
                "The existing JSON export does not contain a top-level 'messages' array."
            );

        writer.WriteNumber("messageCount", total);
        writer.WriteEndObject();
        return total;
    }

    private static long AppendNewMessages(
        JsonElement newRoot,
        Utf8JsonWriter writer,
        CancellationToken cancellationToken
    )
    {
        if (
            newRoot.ValueKind != JsonValueKind.Object
            || !newRoot.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidExportException(
                "The new JSON export does not contain a top-level 'messages' array."
            );
        }

        long count = 0;
        foreach (var message in messages.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            message.WriteTo(writer);
            count++;
        }
        return count;
    }

    private static JsonElement GetConversionData(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("conversionData", out var conversionData)
            ? conversionData
            : default;

    private static bool HasConversionData(JsonElement existing, JsonElement fresh) =>
        existing.ValueKind == JsonValueKind.Object || fresh.ValueKind == JsonValueKind.Object;

    private static void WriteMergedConversionData(
        JsonElement existing,
        JsonElement fresh,
        Utf8JsonWriter writer
    )
    {
        writer.WritePropertyName("conversionData");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", ConversionData.CurrentSchemaVersion);
        WriteMergedArray(writer, "members", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "roles", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "channels", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "emojis", existing, fresh, GetEmojiKey);
        writer.WriteEndObject();
    }

    private static void WriteMergedArray(
        Utf8JsonWriter writer,
        string propertyName,
        JsonElement existing,
        JsonElement fresh,
        Func<JsonElement, string?> getKey
    )
    {
        var valuesByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        AddValues(existing);
        AddValues(fresh);

        writer.WriteStartArray(propertyName);
        foreach (var value in valuesByKey.Values)
            value.WriteTo(writer);

        writer.WriteEndArray();

        void AddValues(JsonElement conversionData)
        {
            if (
                conversionData.ValueKind != JsonValueKind.Object
                || !conversionData.TryGetProperty(propertyName, out var values)
                || values.ValueKind != JsonValueKind.Array
            )
            {
                return;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (getKey(value) is { } key)
                    valuesByKey[key] = value;
            }
        }
    }

    private static string? GetIdKey(JsonElement value) =>
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    private static string? GetEmojiKey(JsonElement value)
    {
        var id =
            value.TryGetProperty("id", out var idProperty)
            && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : "";
        var name =
            value.TryGetProperty("name", out var nameProperty)
            && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()
                : "";
        var isAnimated =
            value.TryGetProperty("isAnimated", out var isAnimatedProperty)
            && isAnimatedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isAnimatedProperty.GetBoolean()
                : false;

        return string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)
            ? null
            : $"{id}|{name}|{isAnimated}";
    }
}
