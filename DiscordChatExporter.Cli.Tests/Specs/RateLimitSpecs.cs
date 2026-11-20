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

public partial class RateLimitSpecs
{
    [Fact]
    public void Archival_request_concurrency_is_conservatively_bounded()
    {
        DiscordClient.UserRequestConcurrencyLimit.Should().Be(1);
        DiscordClient.BotRequestConcurrencyLimit.Should().Be(16);
        DiscordClient.UserRequestStartInterval.Should().Be(TimeSpan.FromMilliseconds(750));
        DiscordClient.InvalidRequestCircuitBreakerThreshold.Should().Be(25);
        DiscordClient
            .UserRequestConcurrencyLimit.Should()
            .BeLessThan(DiscordClient.BotRequestConcurrencyLimit);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(20, 60)]
    public void Repeated_hard_rate_limits_add_a_conservative_cooldown(
        int recentRateLimitCount,
        int expectedSeconds
    )
    {
        DiscordClient
            .GetAdaptiveHardRateLimitCushion(recentRateLimitCount)
            .Should()
            .Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void User_token_advisory_limits_cannot_be_disabled()
    {
        RateLimitPreference.IgnoreAll.IsRespectedFor(TokenKind.User).Should().BeTrue();
        RateLimitPreference.IgnoreAll.IsRespectedFor(TokenKind.Bot).Should().BeFalse();
    }

    [Fact]
    public async Task Uses_discord_json_retry_after_when_429_header_is_missing()
    {
        var observedDelays = new List<TimeSpan>();
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        """{"retry_after":2.5,"global":false}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                },
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
            (delay, _) =>
            {
                observedDelays.Add(delay);
                return ValueTask.CompletedTask;
            }
        );

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().NotBeNull();
        observedDelays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(8.5));
    }

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
                new RateLimitState(true, TimeSpan.FromSeconds(6)),
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
                new RateLimitState(true, TimeSpan.FromSeconds(6)),
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
    public async Task Repeated_unavailable_request_is_suppressed_for_the_rest_of_the_run()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                new HttpResponseMessage(HttpStatusCode.NotFound),
            ])
        );

        using var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var userId = Snowflake.Parse("123456789012345678");

        (await discord.TryGetUserAsync(userId)).Should().BeNull();
        (await discord.TryGetUserAsync(userId)).Should().BeNull();

        var stats = discord.GetRequestStats();
        stats.RequestCount.Should().Be(2);
        stats.AvoidedUnavailableRequestCount.Should().Be(1);
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

    [Fact]
    public async Task Forbidden_optional_channel_is_cached_but_required_channel_still_fails()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(HttpStatusCode.Forbidden)])
        );
        using var discord = new DiscordClient("private-token", RateLimitPreference.IgnoreAll, http);
        var id = Snowflake.Parse("123456789012345678");
        (await discord.TryGetChannelAsync(id)).Should().BeNull();
        (await discord.TryGetChannelAsync(id)).Should().BeNull();
        var required = async () => await discord.GetChannelAsync(id);
        await required.Should().ThrowAsync<DiscordChatExporterException>();
        discord.GetRequestStats().RequestCount.Should().Be(2);
        discord.GetRequestStats().AvoidedUnavailableRequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Optional_channel_does_not_hide_other_errors(HttpStatusCode status)
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(status)])
        );
        using var discord = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        var act = async () =>
            await discord.TryGetChannelAsync(Snowflake.Parse("123456789012345678"));
        await act.Should().ThrowAsync<DiscordChatExporterException>();
    }

    [Fact]
    public async Task Required_channel_can_have_a_forbidden_parent()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"123","type":11,"name":"thread","parent_id":"456"}"""
                    ),
                },
                new(HttpStatusCode.Forbidden),
            ])
        );
        using var discord = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        var channel = await discord.GetChannelAsync(new Snowflake(123));
        channel.Name.Should().Be("thread");
        channel.Parent.Should().BeNull();
    }

    [Fact]
    public async Task Optional_forbidden_channels_still_trip_invalid_request_circuit_breaker()
    {
        var responses = new List<HttpResponseMessage> { new(HttpStatusCode.OK) };
        for (var i = 0; i < DiscordClient.InvalidRequestCircuitBreakerThreshold; i++)
            responses.Add(new(HttpStatusCode.Forbidden));
        using var http = new HttpClient(new QueueHttpMessageHandler(responses));
        using var discord = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        for (ulong i = 1; i < DiscordClient.InvalidRequestCircuitBreakerThreshold; i++)
            (await discord.TryGetChannelAsync(new Snowflake(i))).Should().BeNull();
        var act = async () => await discord.TryGetChannelAsync(new Snowflake(999));
        await act.Should()
            .ThrowAsync<DiscordChatExporterException>()
            .WithMessage("*circuit breaker*");
        // Once tripped, even another channel must fail before a network request.
        var later = async () => await discord.TryGetChannelAsync(new Snowflake(1000));
        await later
            .Should()
            .ThrowAsync<DiscordChatExporterException>()
            .WithMessage("*circuit breaker*");
        discord
            .GetRequestStats()
            .RequestCount.Should()
            .Be(1 + DiscordClient.InvalidRequestCircuitBreakerThreshold);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":10014}", true)]
    [InlineData(HttpStatusCode.NotFound, "", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":50035}", false)]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":\"10014\"}", false)]
    [InlineData(HttpStatusCode.BadRequest, "not json", false)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"code\":10014}", false)]
    [InlineData(HttpStatusCode.Forbidden, "{\"code\":10014}", false)]
    public async Task Reaction_lookup_tolerates_only_missing_or_unknown_emoji(
        HttpStatusCode status,
        string body,
        bool tolerated
    )
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                new(status) { Content = new StringContent(body) },
            ])
        );
        using var discord = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        var count = 0;
        var act = async () =>
        {
            await foreach (
                var user in discord.GetMessageReactionsAsync(
                    Snowflake.Parse("123456789012345678"),
                    Snowflake.Parse("223456789012345678"),
                    new Emoji(Snowflake.Parse("323456789012345678"), "deleted", false)
                )
            )
                count++;
        };
        if (tolerated)
            await act.Should().NotThrowAsync();
        else
            await act.Should().ThrowAsync<DiscordChatExporterException>();
        count.Should().Be(0);
        discord.GetRequestStats().RequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_report_observed_transport_and_status_without_request_data(
        bool bot
    )
    {
        var responses = new List<HttpResponseMessage>();
        if (bot)
            responses.Add(new(HttpStatusCode.Unauthorized));
        responses.Add(new(HttpStatusCode.OK));
        responses.Add(new(HttpStatusCode.Forbidden));
        using var http = new HttpClient(new QueueHttpMessageHandler(responses));
        using var discord = new DiscordClient("private-token", RateLimitPreference.IgnoreAll, http);
        var initial = discord.GetDiagnosticSummary();
        initial
            .Should()
            .Contain("Token kind: not resolved")
            .And.Contain("Last attempted request transport: none");
        discord.GetRequestStats().RequestCount.Should().Be(0);
        await discord.TryGetChannelAsync(Snowflake.Parse("123456789012345678"));
        var summary = discord.GetDiagnosticSummary();
        summary
            .Should()
            .Contain($"Token kind: {(bot ? "Bot" : "User")}")
            .And.Contain("Last attempted request transport: HttpClient")
            .And.Contain("Last HTTP response: 403")
            .And.NotContain("private-token")
            .And.NotContain("123456789012345678");
        if (bot)
            summary.Should().Contain("Selected transport: HttpClient (bot token)");
        discord.GetRequestStats().RequestCount.Should().Be(bot ? 3 : 2);
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
