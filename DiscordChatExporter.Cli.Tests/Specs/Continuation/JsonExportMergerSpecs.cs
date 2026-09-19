using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportMergerSpecs
{
    private static string Existing(string messages, int count) =>
        $$"""
            {
              "guild": { "id": "111", "name": "G" },
              "channel": { "id": "222", "name": "C" },
              "dateRange": { "after": null, "before": null },
              "exportedAt": "2021-01-01T00:00:00+00:00",
              "messages": [{{messages}}],
              "messageCount": {{count}}
            }
            """;

    private const string MsgA =
        """{ "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" }""";
    private const string MsgB =
        """{ "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }""";
    private const string MsgC =
        """{ "id": "3000", "type": "Default", "timestamp": "2021-07-25T10:00:00+00:00", "content": "c" }""";

    // A message with a nested object (author) and nested arrays (reactions, mentions) to
    // exercise the depth-tracking token-pump on non-flat structures.
    private const string MsgNested = """
        {
          "id": "4000",
          "type": "Default",
          "timestamp": "2021-07-26T10:00:00+00:00",
          "content": "nested",
          "author": { "id": "5", "name": "Author", "isBot": false },
          "reactions": [ { "emoji": { "name": "+1" }, "count": 3 } ],
          "mentions": [],
          "attachments": [ { "id": "9", "url": "http://x/y", "fileSizeBytes": 1234 } ]
        }
        """;

    private static async Task<string> WriteAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-merge-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task Merge_wraps_corrupt_new_messages_json_as_InvalidExportException()
    {
        var existing = await WriteAsync(
            """{"guild":{},"channel":{},"messages":[],"messageCount":0}"""
        );
        var existingBefore = await File.ReadAllTextAsync(existing);
        var incoming = await WriteAsync("{ this is not valid json");

        try
        {
            var act = async () =>
                await JsonExportMerger.MergeAsync(existing, incoming, DateTimeOffset.UnixEpoch);

            await act.Should().ThrowAsync<InvalidExportException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
            File.Exists(existing + ".merging.tmp").Should().BeFalse();
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(incoming);
            if (File.Exists(existing + ".merging.tmp"))
                File.Delete(existing + ".merging.tmp");
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task Merge_preserves_existing_file_when_cancelled()
    {
        var existing = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        var existingBefore = await File.ReadAllTextAsync(existing);
        var fresh = await WriteAsync(Existing(MsgC, 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var act = async () =>
                await JsonExportMerger.MergeAsync(
                    existing,
                    fresh,
                    DateTimeOffset.UnixEpoch,
                    cancellation.Token
                );

            await act.Should().ThrowAsync<OperationCanceledException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
        }
    }

    [Fact]
    public async Task It_appends_new_messages_and_fixes_the_count()
    {
        var existing = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        var fresh = await WriteAsync(Existing(MsgC, 1));
        try
        {
            var total = await JsonExportMerger.MergeAsync(
                existing,
                fresh,
                new DateTimeOffset(2026, 06, 03, 0, 0, 0, TimeSpan.Zero)
            );

            total.Should().Be(3);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var root = doc.RootElement;
            var ids = root.GetProperty("messages")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString())
                .ToArray();
            ids.Should().Equal("1000", "2000", "3000");
            root.GetProperty("messageCount").GetInt64().Should().Be(3);
            root.GetProperty("guild").GetProperty("id").GetString().Should().Be("111");
            root.GetProperty("exportedAt").GetString().Should().Contain("2026-06-03");

            // The atomic-replace .bak is a transient crash-safety net, cleaned up on success.
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task Merge_does_not_touch_fixed_temp_or_backup_sentinel_files()
    {
        var existing = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        var fresh = await WriteAsync(Existing(MsgC, 1));
        var oldFixedTempPath = existing + ".merging.tmp";
        var oldFixedBackupPath = existing + ".bak";
        await File.WriteAllTextAsync(oldFixedTempPath, "user temp sentinel");
        await File.WriteAllTextAsync(oldFixedBackupPath, "user backup sentinel");

        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UnixEpoch);

            (await File.ReadAllTextAsync(oldFixedTempPath)).Should().Be("user temp sentinel");
            (await File.ReadAllTextAsync(oldFixedBackupPath)).Should().Be("user backup sentinel");
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            File.Delete(oldFixedTempPath);
            File.Delete(oldFixedBackupPath);
        }
    }

    [Fact]
    public async Task It_appends_into_an_export_that_had_no_messages()
    {
        var existing = await WriteAsync(Existing("", 0));
        var fresh = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        try
        {
            var total = await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            total.Should().Be(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(2);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_rejects_existing_exports_without_a_messages_array()
    {
        var existing = await WriteAsync(
            """
            {
              "guild": { "id": "111", "name": "G" },
              "channel": { "id": "222", "name": "C" },
              "messageCount": 0
            }
            """
        );
        var fresh = await WriteAsync(Existing(MsgA, 1));
        var existingBefore = await File.ReadAllTextAsync(existing);

        try
        {
            var act = async () =>
                await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);

            await act.Should().ThrowAsync<InvalidExportException>();
            (await File.ReadAllTextAsync(existing)).Should().Be(existingBefore);
            File.Exists(existing + ".merging.tmp").Should().BeFalse();
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".merging.tmp"))
                File.Delete(existing + ".merging.tmp");
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_produces_valid_json_whose_count_matches_actual_elements()
    {
        var existing = await WriteAsync(Existing(MsgA, 1));
        var fresh = await WriteAsync(Existing($"{MsgB},{MsgC}", 2));
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var actual = doc.RootElement.GetProperty("messages").GetArrayLength();
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(actual);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_preserves_nested_object_and_array_structure_in_copied_messages()
    {
        // Existing has a nested message; fresh adds another nested message. This guards the
        // depth-tracking CopyValue token-pump against nested objects and arrays.
        var existing = await WriteAsync(Existing(MsgNested, 1));
        var fresh = await WriteAsync(Existing(MsgNested.Replace("\"4000\"", "\"5000\""), 1));
        try
        {
            var total = await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            total.Should().Be(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var root = doc.RootElement;
            var messages = root.GetProperty("messages");
            messages.GetArrayLength().Should().Be(2);
            root.GetProperty("messageCount").GetInt64().Should().Be(2);

            var first = messages[0];
            first.GetProperty("id").GetString().Should().Be("4000");
            first.GetProperty("author").GetProperty("id").GetString().Should().Be("5");
            first.GetProperty("author").GetProperty("isBot").GetBoolean().Should().BeFalse();

            var reactions = first.GetProperty("reactions");
            reactions.GetArrayLength().Should().Be(1);
            reactions[0].GetProperty("emoji").GetProperty("name").GetString().Should().Be("+1");
            reactions[0].GetProperty("count").GetInt32().Should().Be(3);

            first.GetProperty("mentions").GetArrayLength().Should().Be(0);

            var attachments = first.GetProperty("attachments");
            attachments[0].GetProperty("fileSizeBytes").GetInt64().Should().Be(1234);

            // The second (appended) message keeps its rewritten id and nested structure too.
            var second = messages[1];
            second.GetProperty("id").GetString().Should().Be("5000");
            second.GetProperty("author").GetProperty("name").GetString().Should().Be("Author");
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_merges_conversion_data_from_the_fresh_export()
    {
        var existing = await WriteAsync(
            Existing(MsgA, 1)
                .Replace(
                    """
                      "messageCount": 1
                    """,
                    """
                      "messageCount": 1,
                      "conversionData": {
                        "schemaVersion": 1,
                        "members": [ { "id": "10", "displayName": "Alice", "avatarUrl": null, "colorHex": null, "roleIds": [] } ],
                        "roles": [],
                        "channels": [ { "id": "20", "name": "old", "type": "GuildTextChat", "isVoice": false } ],
                        "emojis": []
                      }
                    """
                )
        );
        var fresh = await WriteAsync(
            Existing(MsgB, 1)
                .Replace(
                    """
                      "messageCount": 1
                    """,
                    """
                      "messageCount": 1,
                      "conversionData": {
                        "schemaVersion": 1,
                        "members": [ { "id": "11", "displayName": "Bob", "avatarUrl": null, "colorHex": null, "roleIds": [] } ],
                        "roles": [ { "id": "30", "name": "new-role", "colorHex": null, "position": 1 } ],
                        "channels": [ { "id": "21", "name": "new", "type": "GuildTextChat", "isVoice": false } ],
                        "emojis": [ { "id": "40", "name": "wave", "isAnimated": false, "imageUrl": "wave.png" } ]
                      }
                    """
                )
        );
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var conversionData = doc.RootElement.GetProperty("conversionData");
            conversionData.GetProperty("members").GetArrayLength().Should().Be(2);
            conversionData.GetProperty("roles").GetArrayLength().Should().Be(1);
            conversionData.GetProperty("channels").GetArrayLength().Should().Be(2);
            conversionData.GetProperty("emojis").GetArrayLength().Should().Be(1);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_prefers_fresh_conversion_data_entries_with_the_same_key()
    {
        var existing = await WriteAsync(
            Existing(MsgA, 1)
                .Replace(
                    """
                      "messageCount": 1
                    """,
                    """
                      "messageCount": 1,
                      "conversionData": {
                        "schemaVersion": 1,
                        "members": [ { "id": "10", "displayName": "Old Alice", "avatarUrl": null, "colorHex": null, "roleIds": [] } ],
                        "roles": [ { "id": "30", "name": "old-role", "colorHex": null, "position": 1 } ],
                        "channels": [],
                        "emojis": [ { "id": "40", "name": "wave", "isAnimated": false, "imageUrl": "old-wave.png" } ]
                      }
                    """
                )
        );
        var fresh = await WriteAsync(
            Existing(MsgB, 1)
                .Replace(
                    """
                      "messageCount": 1
                    """,
                    """
                      "messageCount": 1,
                      "conversionData": {
                        "schemaVersion": 1,
                        "members": [ { "id": "10", "displayName": "Fresh Alice", "avatarUrl": null, "colorHex": null, "roleIds": [] } ],
                        "roles": [ { "id": "30", "name": "fresh-role", "colorHex": null, "position": 2 } ],
                        "channels": [],
                        "emojis": [ { "id": "40", "name": "wave", "isAnimated": false, "imageUrl": "fresh-wave.png" } ]
                      }
                    """
                )
        );
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var conversionData = doc.RootElement.GetProperty("conversionData");
            conversionData
                .GetProperty("members")[0]
                .GetProperty("displayName")
                .GetString()
                .Should()
                .Be("Fresh Alice");
            conversionData
                .GetProperty("roles")[0]
                .GetProperty("name")
                .GetString()
                .Should()
                .Be("fresh-role");
            conversionData
                .GetProperty("emojis")[0]
                .GetProperty("imageUrl")
                .GetString()
                .Should()
                .Be("fresh-wave.png");
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_omits_conversion_data_when_neither_export_contains_it()
    {
        var existing = await WriteAsync(Existing(MsgA, 1));
        var fresh = await WriteAsync(Existing(MsgB, 1));
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            doc.RootElement.TryGetProperty("conversionData", out _).Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }
}
