using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class RateLimitSpecs
{
    [Fact]
    public async Task Emits_rate_limit_events_for_retry_after_429_when_advisory_limits_are_disabled()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                CreateRateLimitResponse(TimeSpan.Zero),
                new HttpResponseMessage(HttpStatusCode.OK)
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
                },
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var states = new List<RateLimitState>();
        discord.RateLimitChanged += (_, state) => states.Add(state);

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().NotBeNull();
        states
            .Should()
            .Equal(
                new RateLimitState(true, TimeSpan.FromSeconds(1)),
                new RateLimitState(false, TimeSpan.Zero)
            );
    }

    [Fact]
    public async Task Emits_rate_limit_events_once_for_retry_after_429_when_advisory_limits_are_enabled()
    {
        var rateLimitResponse = CreateRateLimitResponse(TimeSpan.Zero);
        rateLimitResponse.Headers.Add("X-RateLimit-Remaining", "0");
        rateLimitResponse.Headers.Add("X-RateLimit-Reset-After", "0");

        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                rateLimitResponse,
                new HttpResponseMessage(HttpStatusCode.OK)
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
                },
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var states = new List<RateLimitState>();
        discord.RateLimitChanged += (_, state) => states.Add(state);

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().NotBeNull();
        states
            .Should()
            .Equal(
                new RateLimitState(true, TimeSpan.FromSeconds(1)),
                new RateLimitState(false, TimeSpan.Zero)
            );
    }

    [Fact]
    public async Task Disposes_rate_limit_response_before_custom_delay()
    {
        var retryResponse = new TrackingHttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var observedDisposedBeforeDelay = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                retryResponse,
                new HttpResponseMessage(HttpStatusCode.OK)
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
                },
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            async (_, cancellationToken) =>
            {
                observedDisposedBeforeDelay.SetResult(retryResponse.IsDisposed);
                await releaseDelay.Task.WaitAsync(cancellationToken);
            }
        );

        var getUserTask = discord.TryGetUserAsync(Snowflake.Parse("123456789012345678")).AsTask();

        var wasDisposedBeforeDelay = await observedDisposedBeforeDelay.Task.WaitAsync(
            TimeSpan.FromSeconds(5)
        );
        releaseDelay.SetResult();
        var user = await getUserTask.WaitAsync(TimeSpan.FromSeconds(5));

        wasDisposedBeforeDelay.Should().BeTrue();
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Disposes_retried_response_messages()
    {
        var retryResponse = new TrackingHttpResponseMessage(HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                retryResponse,
                new HttpResponseMessage(HttpStatusCode.OK)
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
                },
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        retryResponse.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_cache_token_kind_from_unsuccessful_non_unauthorized_probe()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([new HttpResponseMessage(HttpStatusCode.Forbidden)])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var act = async () => await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        await act.Should().ThrowAsync<DiscordChatExporterException>();
    }

    [Fact]
    public async Task Optional_json_request_returns_null_for_not_found_after_successful_token_probe()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                new HttpResponseMessage(HttpStatusCode.NotFound),
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().BeNull();
    }

    [Fact]
    public async Task Optional_json_request_throws_for_auth_failure_after_successful_token_probe()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var act = async () => await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        await act.Should().ThrowAsync<DiscordChatExporterException>();
    }

    private static HttpResponseMessage CreateRateLimitResponse(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

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

    private sealed class TrackingHttpResponseMessage(HttpStatusCode statusCode)
        : HttpResponseMessage(statusCode)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
