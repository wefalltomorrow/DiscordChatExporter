using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportInspectorSpecs
{
    private static async Task<string> WriteTempAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private const string TwoMessages = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
            { "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }
          ],
          "messageCount": 2
        }
        """;

    private const string NoMessages = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [],
          "messageCount": 0
        }
        """;

    // Same timestamps, but ids are reversed. Snowflakes define message order.
    private const string ReverseOrdered = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "2000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
            { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "b" }
          ],
          "messageCount": 2
        }
        """;

    private const string WithBefore = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": "2021-07-30T00:00:00+00:00" },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
            { "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }
          ],
          "messageCount": 2
        }
        """;

    private const string MixedOrdered = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
            { "id": "3000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" },
            { "id": "2000", "type": "Default", "timestamp": "2021-07-20T13:49:13+00:00", "content": "c" }
          ],
          "messageCount": 3
        }
        """;

    // Messages with no "timestamp" field at all.
    private const string NoTimestamps = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "1000", "type": "Default", "content": "a" },
            { "id": "2000", "type": "Default", "content": "b" }
          ],
          "messageCount": 2
        }
        """;

    [Fact]
    public async Task I_can_read_guild_channel_cutoff_and_count_from_a_valid_export()
    {
        var path = await WriteTempAsync(TwoMessages);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);

            info.GuildId.Value.Should().Be(111UL);
            info.ChannelId.Value.Should().Be(222UL);
            info.LastMessageId.Value.Should().Be(2000UL);
            info.MessageCount.Should().Be(2);
            info.IsChronological.Should().BeTrue();
            info.Before.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_can_detect_a_reverse_ordered_export()
    {
        var path = await WriteTempAsync(ReverseOrdered);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);
            info.IsChronological.Should().BeFalse();
            // The cutoff is still the last element in the array, regardless of order.
            info.LastMessageId.Value.Should().Be(1000UL);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_a_mixed_order_export()
    {
        var path = await WriteTempAsync(MixedOrdered);
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Malformed_snowflakes_are_reported_as_invalid_json_exports()
    {
        var path = await WriteTempAsync(
            """
            {
              "guild": { "id": "not-a-snowflake", "name": "G" },
              "channel": { "id": "222", "name": "C" },
              "messages": [
                { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" }
              ],
              "messageCount": 1
            }
            """
        );
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_can_read_the_before_cutoff_when_the_date_range_has_one()
    {
        var path = await WriteTempAsync(WithBefore);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);
            info.Before.Should().NotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_can_inspect_an_export_whose_messages_have_no_timestamp()
    {
        var path = await WriteTempAsync(NoTimestamps);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);
            info.IsChronological.Should().BeTrue();
            info.LastMessageId.Value.Should().Be(2000UL);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_an_export_with_no_messages()
    {
        var path = await WriteTempAsync(NoMessages);
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_inspect_a_non_dce_json_file()
    {
        var path = await WriteTempAsync("""{ "hello": "world" }""");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_inspect_malformed_json()
    {
        var path = await WriteTempAsync("{ not json");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
