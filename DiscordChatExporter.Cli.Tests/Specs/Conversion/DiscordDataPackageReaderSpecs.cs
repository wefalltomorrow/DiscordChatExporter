using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class DiscordDataPackageReaderSpecs : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DceDataPackage_" + Guid.NewGuid().ToString("N")
    );

    public DiscordDataPackageReaderSpecs() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ParseAsync_reads_official_package_shaped_transcript_and_channel_metadata()
    {
        var channelDirectory = Directory.CreateDirectory(
            Path.Combine(_directory, "Messages", "123456")
        );
        await File.WriteAllTextAsync(
            Path.Combine(channelDirectory.FullName, "channel.json"),
            """
            {
              "Guild ID": "789012",
              "Guild Name": "Example Server",
              "Channel ID": "123456",
              "Channel Name": "general"
            }
            """
        );
        var transcriptPath = Path.Combine(channelDirectory.FullName, "messages.json");
        await File.WriteAllTextAsync(
            transcriptPath,
            """
            [
              {
                "ID": "200",
                "Timestamp": "2025-01-02T03:04:05",
                "Contents": "package message",
                "Attachments": "https://cdn.example/a.png https://cdn.example/b.txt"
              },
              {
                "ID": "100",
                "Timestamp": "2025-01-02T03:00:00Z",
                "Contents": "earlier message",
                "Attachments": ""
              }
            ]
            """
        );

        var parsed = await OfflineExportReader.ParseAsync(transcriptPath);

        parsed.Guild.Id.ToString().Should().Be("789012");
        parsed.Guild.Name.Should().Be("Example Server");
        parsed.Channel.Id.ToString().Should().Be("123456");
        parsed.Channel.Name.Should().Be("general");
        parsed.Channel.Kind.Should().Be(ChannelKind.GuildTextChat);
        parsed.Messages.Should().HaveCount(2);
        parsed.Messages[0].Content.Should().Be("earlier message");
        parsed.Messages[1].Content.Should().Be("package message");
        parsed.Messages[1].Timestamp.Offset.Should().Be(TimeSpan.Zero);
        parsed.Messages[1].Attachments.Should().HaveCount(2);
        parsed.Messages[1].Author.DisplayName.Should().Be("You");
        parsed.ConversionData.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_accepts_empty_transcript_in_c_prefixed_channel_folder()
    {
        var channelDirectory = Directory.CreateDirectory(Path.Combine(_directory, "c123456"));
        var transcriptPath = Path.Combine(channelDirectory.FullName, "messages.json");
        await File.WriteAllTextAsync(transcriptPath, "[]");

        var parsed = await OfflineExportReader.ParseAsync(transcriptPath);

        parsed.Channel.Id.ToString().Should().Be("123456");
        parsed.Guild.IsDirect.Should().BeTrue();
        parsed.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_rejects_non_transcript_json_with_domain_error()
    {
        var path = Path.Combine(_directory, "not-a-transcript.json");
        await File.WriteAllTextAsync(path, """{ "hello": "world" }""");

        var act = () => OfflineExportReader.ParseAsync(path).AsTask();

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task ParseAsync_wraps_malformed_json_with_domain_error()
    {
        var path = Path.Combine(_directory, "broken.json");
        await File.WriteAllTextAsync(path, "[");

        var act = () => OfflineExportReader.ParseAsync(path).AsTask();

        await act.Should().ThrowAsync<InvalidExportException>();
    }
}
