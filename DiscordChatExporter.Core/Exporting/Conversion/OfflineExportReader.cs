using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Dispatches supported offline JSON inputs without making any Discord API requests.
public static class OfflineExportReader
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
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("guild", out _)
                && root.TryGetProperty("channel", out _)
                && root.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
            )
            {
                return await JsonExportReader.ParseAsync(filePath, cancellationToken);
            }

            return await DiscordDataPackageReader.ParseAsync(filePath, root, cancellationToken);
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
                        or InvalidOperationException
                        or OverflowException
            )
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a supported offline chat transcript.",
                ex
            );
        }
    }
}
