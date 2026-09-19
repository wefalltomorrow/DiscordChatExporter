using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class JsonConversionDataSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceConversion_" + Guid.NewGuid().ToString("N")
    );

    public JsonConversionDataSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ExportContext CreateContext(string outputPath)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            "topic",
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
            null,
            null,
            false,
            content,
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null
        );

    private static void SeedContext(
        ExportContext context,
        Member member,
        Role role,
        Channel channel
    )
    {
        var members = GetPrivateDictionary<Member?>(context, "_membersById");
        members[member.Id] = member;

        var roles = GetPrivateDictionary<Role>(context, "_rolesById");
        roles[role.Id] = role;

        var channels = GetPrivateDictionary<Channel?>(context, "_channelsById");
        channels[channel.Id] = channel;
    }

    private static Dictionary<Snowflake, TValue> GetPrivateDictionary<TValue>(
        ExportContext context,
        string fieldName
    ) =>
        (Dictionary<Snowflake, TValue>)
            typeof(ExportContext)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(context)!;

    [Fact]
    public async Task Json_writer_keeps_inline_emojis_with_same_name_and_different_ids()
    {
        var path = Path.Combine(_dir, "same-name-emojis.json");
        var author = CreateUser(10, "alice");

        await using (var writer = new JsonMessageWriter(File.Create(path), CreateContext(path)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(1001, author, "<:same:100000000000000001> <:same:100000000000000002>")
            );
            await writer.WritePostambleAsync();
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        document
            .RootElement.GetProperty("messages")[0]
            .GetProperty("inlineEmojis")
            .EnumerateArray()
            .Select(emoji => emoji.GetProperty("id").GetString())
            .Should()
            .Equal("100000000000000001", "100000000000000002");
    }

    [Fact]
    public async Task Json_writer_emits_conversion_data_from_context_caches()
    {
        var path = Path.Combine(_dir, "chat.json");
        var author = CreateUser(10, "alice");
        var role = new Role(new Snowflake(20), "red", 1, Color.FromArgb(255, 255, 0, 0));
        var member = new Member(
            author,
            "Alice Display",
            "https://cdn.example/avatar.png",
            [role.Id]
        );
        var channel = new Channel(
            new Snowflake(30),
            ChannelKind.GuildVoiceChat,
            new Snowflake(1),
            null,
            "voice-channel",
            0,
            null,
            null,
            false,
            null
        );
        var context = CreateContext(path);
        SeedContext(context, member, role, channel);

        await using (var writer = new JsonMessageWriter(File.Create(path), context))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(CreateMessage(1001, author, "hello"));
            await writer.WritePostambleAsync();
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var conversionData = document.RootElement.GetProperty("conversionData");

        conversionData
            .GetProperty("schemaVersion")
            .GetInt32()
            .Should()
            .Be(ConversionData.CurrentSchemaVersion);
        conversionData
            .GetProperty("members")[0]
            .GetProperty("displayName")
            .GetString()
            .Should()
            .Be("Alice Display");
        conversionData
            .GetProperty("roles")[0]
            .GetProperty("colorHex")
            .GetString()
            .Should()
            .Be("#FF0000");
        conversionData
            .GetProperty("channels")[0]
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("voice-channel");
        conversionData
            .GetProperty("channels")[0]
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("GuildVoiceChat");
        conversionData
            .GetProperty("channels")[0]
            .GetProperty("isVoice")
            .GetBoolean()
            .Should()
            .BeTrue();
    }
}
