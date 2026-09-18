using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestReaderSpecs
{
    [Fact]
    public async Task Reading_a_missing_file_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        var result = await ManifestReader.TryReadAsync(path);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_garbage_file_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json ]");
        try
        {
            var result = await ManifestReader.TryReadAsync(path);
            result.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reading_a_valid_manifest_round_trips_its_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        var json = """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-06-03T10:00:00+00:00",
              "entries": [
                {
                  "guildId": "111",
                  "guildName": "My Server",
                  "channelId": "222",
                  "channelName": "general",
                  "categoryName": "Text",
                  "file": "My Server - general [222].json",
                  "format": "Json",
                  "messageCount": 42,
                  "firstMessageId": "300",
                  "firstMessageTimestamp": "2026-06-01T00:00:00+00:00",
                  "lastMessageId": "900",
                  "lastMessageTimestamp": "2026-06-02T00:00:00+00:00",
                  "assetCount": 5,
                  "fileSizeBytes": 1234,
                  "sha256": "abc123",
                  "partitioned": false,
                  "exportedAt": "2026-06-03T10:00:00+00:00"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, json);
        try
        {
            var result = await ManifestReader.TryReadAsync(path);

            result.Should().NotBeNull();
            result!.SchemaVersion.Should().Be(1);
            result.Entries.Should().HaveCount(1);
            var entry = result.Entries[0];
            entry.ChannelId.Should().Be("222");
            entry.File.Should().Be("My Server - general [222].json");
            entry.MessageCount.Should().Be(42);
            entry.LastMessageId.Should().Be("900");
            entry.Sha256.Should().Be("abc123");
            entry.Partitioned.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_normalizes_a_null_entries_collection_to_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            /* lang=json */
            """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-01-01T00:00:00+00:00",
              "entries": null
            }
            """
        );

        try
        {
            var manifest = await ManifestReader.TryReadAsync(path);

            manifest.Should().NotBeNull();
            manifest!.Entries.Should().NotBeNull().And.BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_drops_null_entry_elements()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        var json = """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-01-01T00:00:00+00:00",
              "entries": [
                null,
                {
                  "guildId": "1",
                  "guildName": "Guild",
                  "channelId": "5",
                  "channelName": "general",
                  "categoryName": null,
                  "file": "chat [5].json",
                  "format": "Json",
                  "messageCount": 0,
                  "firstMessageId": null,
                  "firstMessageTimestamp": null,
                  "lastMessageId": null,
                  "lastMessageTimestamp": null,
                  "assetCount": 0,
                  "fileSizeBytes": 0,
                  "sha256": "",
                  "partitioned": false,
                  "exportedAt": "2026-01-01T00:00:00+00:00"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, json);

        try
        {
            var manifest = await ManifestReader.TryReadAsync(path);

            manifest!.Entries.Should().ContainSingle().Which.File.Should().Be("chat [5].json");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reading_a_future_schema_version_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            /* lang=json */
            """
            {
              "schemaVersion": 999,
              "generatedAt": "2026-01-01T00:00:00+00:00",
              "entries": []
            }
            """
        );

        try
        {
            var manifest = await ManifestReader.TryReadAsync(path);

            manifest.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
