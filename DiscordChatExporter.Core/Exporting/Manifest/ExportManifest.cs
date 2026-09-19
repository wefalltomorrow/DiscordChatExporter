using System;
using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public sealed record ExportManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ManifestEntry> Entries
)
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "manifest.json";
}
