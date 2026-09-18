using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestReader
{
    // Reads and parses a manifest.json. Returns null (never throws) when the file is missing,
    // unreadable, or not valid manifest JSON, so callers can treat "no usable manifest" uniformly.
    public static async ValueTask<ExportManifest?> TryReadAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(filePath);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.ExportManifest,
                cancellationToken
            );

            if (manifest is null)
                return null;

            if (manifest.SchemaVersion > ExportManifest.CurrentSchemaVersion)
                return null;

            // STJ source-gen doesn't enforce non-null refs: a valid-but-incomplete manifest can
            // carry a null Entries collection or null elements. Normalize here so every consumer
            // can rely on non-null Entries with non-null elements.
            var entries = (manifest.Entries ?? []).Where(e => e is not null).ToArray();
            return manifest.Entries is not null && entries.Length == manifest.Entries.Count
                ? manifest
                : manifest with
                {
                    Entries = entries,
                };
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or IOException
                        or NotSupportedException
                        or UnauthorizedAccessException
            )
        {
            return null;
        }
    }
}
