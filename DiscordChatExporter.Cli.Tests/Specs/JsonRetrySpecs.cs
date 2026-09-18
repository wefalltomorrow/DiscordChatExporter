using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class JsonRetrySpecs
{
    [Fact]
    public async Task Retries_the_exact_json_request_after_a_truncated_response()
    {
        var handler = new QueueHttpMessageHandler(
            [
                // Token-kind probe.
                new HttpResponseMessage(HttpStatusCode.OK),

                // First user request: syntactically truncated JSON.
                JsonResponse("""{"id":"123456789012345678","username":"test"""),

                // Retry of the same user request: valid response.
                JsonResponse(
                    """
                    {
                      "id": "123456789012345678",
                      "username": "test-user",
                      "global_name": "Test User",
                      "discriminator": "0",
                      "avatar": null
                    }
                    """
                ),
            ]
        );

        using var httpClient = new HttpClient(handler);
        using var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().NotBeNull();
        user!.Name.Should().Be("test-user");
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task Exhausted_truncated_json_retries_become_a_non_fatal_exporter_exception()
    {
        var responses = new List<HttpResponseMessage>
        {
            // Token-kind probe.
            new(HttpStatusCode.OK),
        };

        for (var i = 0; i < 5; i++)
            responses.Add(JsonResponse("""{"id":"123456789012345678","username":"test"""));

        var handler = new QueueHttpMessageHandler(responses);
        using var httpClient = new HttpClient(handler);
        using var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var act = async () =>
            await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        var exception = await act.Should().ThrowAsync<DiscordChatExporterException>();

        exception.Which.IsFatal.Should().BeFalse();
        exception.Which.Message.Should().Contain("malformed or truncated JSON");
        exception.Which.InnerException.Should().NotBeNull();
        handler.RequestCount.Should().Be(6);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class QueueHttpMessageHandler(IReadOnlyCollection<HttpResponseMessage> responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;

            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Unexpected HTTP request.");

            return Task.FromResult(response);
        }
    }
}
