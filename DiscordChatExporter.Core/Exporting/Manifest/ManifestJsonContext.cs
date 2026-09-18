using System.Text.Json.Serialization;

namespace DiscordChatExporter.Core.Exporting.Manifest;

// Source-generated JSON for the manifest. Using a JsonSerializerContext (instead of the
// reflection-based JsonSerializer overloads) keeps manifest (de)serialization trim-safe, so the
// GUI still publishes under PublishTrimmed=true — reflection-based JSON triggers IL2026 and can
// silently drop trimmed property types at runtime. These options reproduce the previous settings
// exactly (camelCase, indented, case-insensitive reads) so existing manifest.json files round-trip
// unchanged. ExportManifest transitively covers ManifestEntry; all fields are primitives.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(ExportManifest))]
internal partial class ManifestJsonContext : JsonSerializerContext;
