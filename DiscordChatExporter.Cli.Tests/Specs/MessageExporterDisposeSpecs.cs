using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class MessageExporterDisposeSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceMessageExporterDispose_" + Guid.NewGuid().ToString("N")
    );

    public MessageExporterDisposeSpecs() => Directory.CreateDirectory(_dir);

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

    private static ExportContext CreateContext(string outputPath) =>
        new(
            new DiscordClient("fake-token"),
            new ExportRequest(
                new Guild(new Snowflake(1), "Test Guild", ""),
                new Channel(
                    new Snowflake(2),
                    ChannelKind.GuildTextChat,
                    new Snowflake(1),
                    null,
                    "general",
                    null,
                    null,
                    null,
                    false,
                    null
                ),
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
            )
        );

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, DateTimeOffset timestamp) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            timestamp,
            null,
            null,
            false,
            "hello",
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

    [Fact]
    public async Task Dispose_with_canceled_token_does_not_create_an_empty_export()
    {
        var outputPath = Path.Combine(_dir, "canceled.json");
        var exporter = new MessageExporter(CreateContext(outputPath));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await exporter.DisposeAsync(cancellation.Token);

        File.Exists(outputPath).Should().BeFalse();
    }

    [Fact]
    public async Task File_stats_track_chronological_first_and_last_messages()
    {
        var outputPath = Path.Combine(_dir, "reverse.json");
        await using var exporter = new MessageExporter(CreateContext(outputPath));
        var author = CreateUser(1, "alice");
        var older = CreateMessage(
            100,
            author,
            new DateTimeOffset(2021, 07, 24, 13, 49, 13, TimeSpan.Zero)
        );
        var newer = CreateMessage(
            200,
            author,
            new DateTimeOffset(2021, 07, 25, 10, 00, 00, TimeSpan.Zero)
        );

        await exporter.ExportMessageAsync(newer);
        await exporter.ExportMessageAsync(older);
        await exporter.DisposeAsync();

        var file = exporter.Files.Should().ContainSingle().Subject;
        file.FirstMessageId.Should().Be(older.Id);
        file.FirstMessageTimestamp.Should().Be(older.Timestamp);
        file.LastMessageId.Should().Be(newer.Id);
        file.LastMessageTimestamp.Should().Be(newer.Timestamp);
    }
}
