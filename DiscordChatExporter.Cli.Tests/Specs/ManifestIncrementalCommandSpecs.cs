using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class ManifestIncrementalCommandSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceCombined_" + Guid.NewGuid().ToString("N")
    );

    public ManifestIncrementalCommandSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Incremental_command_checkpoints_fresh_exports_and_appends_from_actual_archive(
        bool resume,
        bool checkpoint,
        bool staleManifest
    )
    {
        var path = Path.Combine(_dir, "archive.json");
        await RunAsync(path, 1001, resume, checkpoint);
        var manifest = (
            await ManifestReader.TryReadAsync(Path.Combine(_dir, ExportManifest.FileName))
        )!;
        manifest.Entries.Should().ContainSingle().Which.MessageCount.Should().Be(1);
        if (staleManifest)
        {
            // The manifest reports progress beyond the last message actually saved.
            await ManifestWriter.WriteAsync(
                _dir,
                [
                    manifest.Entries[0] with
                    {
                        LastMessageId = "9999",
                        MessageCount = 99,
                        Sha256 = "stale",
                    },
                ],
                DateTimeOffset.UtcNow
            );
        }

        var handler = await RunAsync(path, 1002, resume, checkpoint);
        var cutoff = await ContinuationFormat.ReadCutoffAsync(path, ExportFormat.Json);
        cutoff.ExistingCount.Should().Be(2);
        cutoff.Cutoff.Value.Should().Be(1002);
        handler.Requests.Should().Contain(p => p.Contains("after=1001"));
        handler.Requests.Count(p => p == "/api/v10/guilds/1").Should().Be(1);
        handler.Requests.Count(p => p == "/api/v10/guilds/1/channels").Should().Be(1);
        handler.Requests.Count(p => p == "/api/v10/guilds/1/roles").Should().Be(1);
        manifest = (
            await ManifestReader.TryReadAsync(Path.Combine(_dir, ExportManifest.FileName))
        )!;
        manifest.Entries.Should().ContainSingle().Which.MessageCount.Should().Be(2);

        // Repeating the same command must not duplicate the newest message.
        await RunAsync(path, 1002, resume, checkpoint);
        (await ContinuationFormat.ReadCutoffAsync(path, ExportFormat.Json))
            .ExistingCount.Should()
            .Be(2);
    }

    [Fact]
    public async Task Resume_without_incremental_still_skips_completed_archives()
    {
        var path = Path.Combine(_dir, "archive.json");
        await RunAsync(path, 1001, true, false);
        var handler = await RunAsync(path, 1002, true, false, incremental: false);
        (await ContinuationFormat.ReadCutoffAsync(path, ExportFormat.Json))
            .ExistingCount.Should()
            .Be(1);
        handler.Requests.Should().NotContain(p => p.Contains("/messages"));
    }

    private static async Task<ArchiveHandler> RunAsync(
        string path,
        ulong newest,
        bool resume,
        bool checkpoint,
        bool incremental = true
    )
    {
        var handler = new ArchiveHandler(newest);
        using var http = new HttpClient(handler);
        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            http,
            (_, _) => ValueTask.CompletedTask
        );
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "chat",
            0,
            null,
            null,
            false,
            new Snowflake(newest)
        );
        var command = new TestCommand(discord, channel)
        {
            Token = "test-token",
            OutputPath = path,
            ExportFormat = ExportFormat.Json,
            ShouldIncremental = incremental,
            ShouldResume = resume,
            ShouldCheckpoint = checkpoint,
            ShouldFormatMarkdown = false,
            IsUkraineSupportMessageDisabled = true,
        };
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, output, error);
        await command.ExecuteAsync(console);
        return handler;
    }

    private sealed class TestCommand(DiscordClient client, Channel channel) : ExportCommandBase
    {
        protected override DiscordClient CreateDiscordClient() => client;

        protected override ValueTask ExecuteCoreAsync(IConsole console) =>
            ExportAsync(console, [channel]);
    }

    private sealed class ArchiveHandler(ulong newest) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            var body = uri.AbsolutePath switch
            {
                "/api/v10/users/@me" =>
                    "{\"id\":\"10\",\"username\":\"alice\",\"discriminator\":\"0\",\"avatar\":null}",
                "/api/v10/guilds/1" => "{\"id\":\"1\",\"name\":\"Guild\",\"icon\":null}",
                "/api/v10/guilds/1/channels" or "/api/v10/guilds/1/roles" => "[]",
                "/api/v10/guilds/1/members/10" => "{}",
                "/api/v10/channels/2/messages" => Messages(uri.Query),
                _ => throw new InvalidOperationException("Unexpected request: " + uri.PathAndQuery),
            };
            return Task.FromResult(
                new HttpResponseMessage(
                    uri.AbsolutePath.EndsWith("/members/10")
                        ? HttpStatusCode.NotFound
                        : HttpStatusCode.OK
                )
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }

        private string Messages(string query)
        {
            var match = Regex.Match(query, @"after=(\d+)");
            if (match.Success && ulong.Parse(match.Groups[1].Value) >= newest)
                return "[]";
            return $$"""
                [{"id":"{{newest}}","type":0,"flags":0,"author":{"id":"10","username":"alice","discriminator":"0","avatar":null},"timestamp":"2026-01-01T00:00:00Z","content":"message {{newest}}","attachments":[],"embeds":[],"mentions":[]}]
                """;
        }
    }
}
