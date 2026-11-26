using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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

// A channel the exporting account cannot access must never abort an export.
// https://github.com/wefalltomorrow/DiscordChatExporter/issues/19
public sealed class InaccessibleChannelSpecs
{
    private static readonly Snowflake GuildId = new(9);
    private static readonly Snowflake ThreadChannelId = new(400);
    private static readonly Snowflake ParentChannelId = new(401);
    private static readonly Snowflake StarterPlaceholderId = new(500);
    private static readonly Snowflake OriginalMessageId = new(600);
    private static readonly Snowflake ReplyMessageId = new(501);

    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string UserJson = """
        {
          "id": "123456789012345678",
          "username": "test-user",
          "global_name": "Test User",
          "discriminator": "0",
          "avatar": null
        }
        """;

    [Fact]
    public async Task Channel_mention_of_an_inaccessible_channel_renders_a_placeholder()
    {
        using var httpClient = new HttpClient(
            new FakeDiscordHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;

                if (path.EndsWith("/users/@me", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, UserJson);

                if (path.EndsWith("/channels/999", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })
        );
        using var discord = CreateClient(httpClient);
        var context = CreateContext(discord);

        var output = await PlainTextMarkdownVisitor.FormatAsync(context, "ask in <#999> instead");

        output.Should().Be("ask in #deleted-channel instead");
    }

    [Fact]
    public async Task Thread_starter_in_an_inaccessible_parent_channel_is_skipped()
    {
        using var httpClient = new HttpClient(new FakeDiscordHandler(CreateThreadResponse));
        using var discord = CreateClient(httpClient);

        var messages = await CollectAsync(discord.GetMessagesAsync(ThreadChannelId));

        messages.Should().ContainSingle().Which.Id.Should().Be(ReplyMessageId);
    }

    [Fact]
    public async Task Reverse_export_skips_a_thread_starter_in_an_inaccessible_parent_channel()
    {
        using var httpClient = new HttpClient(new FakeDiscordHandler(CreateThreadResponse));
        using var discord = CreateClient(httpClient);

        var messages = await CollectAsync(discord.GetMessagesInReverseAsync(ThreadChannelId));

        messages.Should().ContainSingle().Which.Id.Should().Be(ReplyMessageId);
    }

    private static HttpResponseMessage CreateThreadResponse(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        if (path.EndsWith("/users/@me", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, UserJson);

        // The parent channel of the thread is no longer accessible to this account.
        if (path.EndsWith($"/channels/{ParentChannelId}/messages", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.Forbidden);

        if (path.EndsWith($"/channels/{ThreadChannelId}/messages", StringComparison.Ordinal))
        {
            // Paging past the start of the history returns nothing.
            if (uri.Query.Contains("before=", StringComparison.Ordinal))
                return MessagesResponse();

            if (uri.Query.Contains($"after={ReplyMessageId}", StringComparison.Ordinal))
                return MessagesResponse();

            // Boundary snapshot requests only ask for a single message.
            if (uri.Query.Contains("limit=100", StringComparison.Ordinal))
                return MessagesResponse(
                    CreateMessageJson(ReplyMessageId, "second"),
                    CreateThreadStarterJson()
                );

            // Discord returns messages newest to oldest.
            return MessagesResponse(CreateThreadStarterJson());
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string CreateThreadStarterJson() =>
        $$"""
            {
              "id": "{{StarterPlaceholderId}}",
              "type": 21,
              "flags": 0,
              "author": {
                "id": "10",
                "username": "alice",
                "global_name": "Alice",
                "discriminator": "0",
                "avatar": null
              },
              "timestamp": "{{Timestamp:O}}",
              "content": "",
              "attachments": [],
              "embeds": [],
              "mentions": [],
              "message_reference": {
                "type": 0,
                "channel_id": "{{ParentChannelId}}",
                "message_id": "{{OriginalMessageId}}",
                "guild_id": "{{GuildId}}"
              }
            }
            """;

    private static string CreateMessageJson(Snowflake id, string content) =>
        $$"""
            {
              "id": "{{id}}",
              "type": 0,
              "flags": 0,
              "author": {
                "id": "10",
                "username": "alice",
                "global_name": "Alice",
                "discriminator": "0",
                "avatar": null
              },
              "timestamp": "{{Timestamp:O}}",
              "content": "{{content}}",
              "attachments": [],
              "embeds": [],
              "mentions": []
            }
            """;

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage MessagesResponse(params string[] messages) =>
        Json(HttpStatusCode.OK, "[" + string.Join(",", messages) + "]");

    private static DiscordClient CreateClient(HttpClient httpClient) =>
        new(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

    private static ExportContext CreateContext(DiscordClient discord)
    {
        var guild = new Guild(GuildId, "Test Guild", "");
        var channel = new Channel(
            ThreadChannelId,
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "thread",
            null,
            null,
            null,
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt"),
            null,
            ExportFormat.PlainText,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

        return new ExportContext(discord, request);
    }

    private static async Task<IReadOnlyList<Message>> CollectAsync(
        IAsyncEnumerable<Message> messages
    )
    {
        var result = new List<Message>();
        await foreach (var message in messages)
            result.Add(message);

        return result;
    }

    private sealed class FakeDiscordHandler(
        Func<HttpRequestMessage, HttpResponseMessage> createResponse
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(createResponse(request));
    }
}
