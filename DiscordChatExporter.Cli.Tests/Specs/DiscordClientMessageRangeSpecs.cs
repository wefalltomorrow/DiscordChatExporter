using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class DiscordClientMessageRangeSpecs
{
    [Fact]
    public async Task Forward_message_export_excludes_the_exact_before_boundary_id()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseId = Snowflake.FromDate(instant).Value;
        var lastInRange = new Snowflake(baseId + 1);
        var before = new Snowflake(baseId + 2);

        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                CreateUserResponse(),
                CreateMessagesResponse(MessageJson(lastInRange, instant, "last in range")),
                CreateMessagesResponse(
                    MessageJson(before, instant, "boundary"),
                    MessageJson(lastInRange, instant, "last in range")
                ),
            ])
        );
        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var messages = await CollectAsync(
            discord.GetMessagesAsync(new Snowflake(123), before: before)
        );

        messages.Should().ContainSingle().Which.Id.Should().Be(lastInRange);
    }

    [Fact]
    public async Task Reverse_message_export_excludes_messages_at_or_before_after_boundary()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseId = Snowflake.FromDate(instant).Value;
        var after = new Snowflake(baseId + 1);
        var firstInRange = new Snowflake(baseId + 2);
        var newest = new Snowflake(baseId + 3);

        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                CreateUserResponse(),
                CreateMessagesResponse(MessageJson(firstInRange, instant, "first in range")),
                CreateMessagesResponse(
                    MessageJson(newest, instant, "newest"),
                    MessageJson(firstInRange, instant, "first in range"),
                    MessageJson(after, instant, "boundary")
                ),
            ])
        );
        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var messages = await CollectAsync(
            discord.GetMessagesInReverseAsync(new Snowflake(123), after)
        );

        messages.Select(m => m.Id).Should().Equal(newest, firstInRange);
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

    private static HttpResponseMessage CreateUserResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "id": "123456789012345678",
                  "username": "test-user",
                  "global_name": "Test User",
                  "discriminator": "0",
                  "avatar": null
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static HttpResponseMessage CreateMessagesResponse(params string[] messages) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "[" + string.Join(",", messages) + "]",
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static string MessageJson(Snowflake id, DateTimeOffset timestamp, string content) =>
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
              "timestamp": "{{timestamp:O}}",
              "content": "{{content}}",
              "attachments": [],
              "embeds": [],
              "mentions": []
            }
            """;

    private sealed class QueueHttpMessageHandler(IReadOnlyCollection<HttpResponseMessage> responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Unexpected HTTP request.");

            return Task.FromResult(response);
        }
    }
}
