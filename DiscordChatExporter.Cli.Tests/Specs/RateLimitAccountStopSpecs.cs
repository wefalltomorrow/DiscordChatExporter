using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 40002)]
    [InlineData(HttpStatusCode.BadRequest, 40002)]
    [InlineData(HttpStatusCode.Forbidden, 40333)]
    public async Task Account_restrictions_stop_optional_requests_and_later_channels(
        HttpStatusCode status,
        int code
    )
    {
        var restricted = new TrackingHttpResponseMessage(status)
        {
            Content = new StringContent(
                $$"""{"code":{{code}},"message":"private response text"}"""
            ),
        };
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), restricted])
        );
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);

        try
        {
            using var client = new DiscordClient(
                "private-token",
                RateLimitPreference.IgnoreAll,
                http,
                userTokenRequestStopStore: stopStore
            );
            for (ulong id = 1; id <= 3; id++)
            {
                var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
                var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
                failure.Which.IsFatal.Should().BeTrue();
                failure
                    .Which.Message.Should()
                    .Contain(code.ToString())
                    .And.NotContain("private response text");
            }
            restricted.IsDisposed.Should().BeTrue();
            client.GetRequestStats().RequestCount.Should().Be(2);
            client
                .GetDiagnosticSummary()
                .Should()
                .Contain("Request stop reason: Discord")
                .And.NotContain("private-token");

            stopStore.TryGetStopReason("private-token", out var recordedReason).Should().BeTrue();
            recordedReason.Should().Contain(code.ToString());
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"code\":50001}")]
    [InlineData("{\"code\":50013}")]
    [InlineData("{\"code\":\"40002\"}")]
    [InlineData("not json")]
    public async Task Ordinary_optional_permission_errors_do_not_stop_other_channels(string body)
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                new(HttpStatusCode.Forbidden) { Content = new StringContent(body) },
                new(HttpStatusCode.NotFound),
            ])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        (await client.TryGetChannelAsync(new Snowflake(1))).Should().BeNull();
        (await client.TryGetChannelAsync(new Snowflake(2))).Should().BeNull();
        client.GetRequestStats().RequestCount.Should().Be(3);
        client.GetDiagnosticSummary().Should().Contain("Request stop reason: none");
    }

    [Fact]
    public async Task Authentication_loss_stops_requests_after_successful_probe()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(HttpStatusCode.Unauthorized)])
        );

        try
        {
            using var client = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                http,
                userTokenRequestStopStore: new UserTokenRequestStopStore(stopDirectory)
            );
            for (ulong id = 1; id <= 2; id++)
            {
                var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
                (await act.Should().ThrowAsync<DiscordChatExporterException>())
                    .Which.IsFatal.Should()
                    .BeTrue();
            }
            client.GetRequestStats().RequestCount.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task User_authentication_loss_records_a_persistent_stop()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(HttpStatusCode.Unauthorized)])
        );

        try
        {
            using (
                var client = new DiscordClient(
                    "test-token",
                    RateLimitPreference.IgnoreAll,
                    http,
                    userTokenRequestStopStore: stopStore
                )
            )
            {
                var act = async () => await client.TryGetChannelAsync(new Snowflake(1));
                var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
                failure.Which.IsFatal.Should().BeTrue();
                failure.Which.Message.Should().Contain("401").And.Contain("standing");
                client.GetRequestStats().RequestCount.Should().Be(2);
            }

            stopStore.TryGetStopReason("test-token", out var recordedReason).Should().BeTrue();
            recordedReason.Should().Contain("401");

            var restartedRequests = 0;
            using var restartedHttp = new HttpClient(
                new CallbackHandler(
                    (_, _) =>
                    {
                        Interlocked.Increment(ref restartedRequests);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                    }
                )
            );
            using var restartedClient = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                restartedHttp,
                userTokenRequestStopStore: new UserTokenRequestStopStore(stopDirectory)
            );

            var restartedAct = async () =>
                await restartedClient.TryGetChannelAsync(new Snowflake(1));
            (await restartedAct.Should().ThrowAsync<DiscordChatExporterException>())
                .Which.Message.Should()
                .Contain("HTTP 401");
            restartedRequests.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Bot_authentication_loss_is_not_recorded_as_a_user_token_stop()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-bot-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.Unauthorized),
                new(HttpStatusCode.OK),
                new(HttpStatusCode.Unauthorized),
            ])
        );
        using var client = new DiscordClient(
            "bot-token",
            RateLimitPreference.IgnoreAll,
            http,
            userTokenRequestStopStore: stopStore
        );

        var act = async () => await client.TryGetChannelAsync(new Snowflake(1));
        (await act.Should().ThrowAsync<DiscordChatExporterException>())
            .Which.IsFatal.Should()
            .BeTrue();

        stopStore.TryGetStopReason("bot-token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_token_is_probed_once_in_each_mode_then_stops()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.Unauthorized),
                new(HttpStatusCode.Unauthorized),
            ])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        for (ulong id = 1; id <= 2; id++)
        {
            var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
            await act.Should().ThrowAsync<DiscordChatExporterException>().WithMessage("*invalid*");
        }
        client.GetRequestStats().RequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_operations_share_one_token_resolution(bool bot)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        using var http = new HttpClient(
            new CallbackHandler(
                async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                    {
                        Interlocked.Increment(ref probes);
                        entered.TrySetResult();
                        await release.Task.WaitAsync(ct);
                        return new HttpResponseMessage(
                            bot
                            && request.Headers.GetValues("Authorization").Single() == "test-token"
                                ? HttpStatusCode.Unauthorized
                                : HttpStatusCode.OK
                        );
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            )
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        var first = client.TryGetChannelAsync(new Snowflake(1)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = Enumerable
            .Range(2, 8)
            .Select(id => client.TryGetChannelAsync(new Snowflake((ulong)id)).AsTask())
            .ToArray();
        release.TrySetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        probes.Should().Be(bot ? 2 : 1);
    }

    [Fact]
    public async Task Queued_user_requests_stop_before_sending_after_account_restriction()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(
            new CallbackHandler(
                async (request, ct) =>
                {
                    Interlocked.Increment(ref requests);
                    if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"code\":40002}"),
                    };
                }
            )
        );
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            http,
            userTokenRequestStopStore: new UserTokenRequestStopStore(stopDirectory)
        );
        var first = client.TryGetChannelAsync(new Snowflake(1)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = Enumerable
            .Range(2, 8)
            .Select(id => client.TryGetChannelAsync(new Snowflake((ulong)id)).AsTask())
            .ToArray();
        release.TrySetResult();
        foreach (var task in pending.Append(first))
        {
            var act = async () => await task.WaitAsync(TimeSpan.FromSeconds(5));
            (await act.Should().ThrowAsync<DiscordChatExporterException>())
                .Which.IsFatal.Should()
                .BeTrue();
        }
        requests.Should().Be(2);

        if (Directory.Exists(stopDirectory))
            Directory.Delete(stopDirectory, recursive: true);
    }

    [Fact]
    public async Task User_rate_limit_stops_queued_requests_without_retry()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var delays = 0;
        var rateLimited = new TrackingHttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(
            new CallbackHandler(
                async (request, ct) =>
                {
                    Interlocked.Increment(ref requests);
                    if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return rateLimited;
                }
            )
        );
        try
        {
            using var client = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                http,
                (_, _) =>
                {
                    Interlocked.Increment(ref delays);
                    return ValueTask.CompletedTask;
                },
                stopStore
            );

            var first = client.TryGetChannelAsync(new Snowflake(1)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Enumerable
                .Range(2, 8)
                .Select(id => client.TryGetChannelAsync(new Snowflake((ulong)id)).AsTask())
                .ToArray();
            release.TrySetResult();

            foreach (var task in pending.Append(first))
            {
                var act = async () => await task.WaitAsync(TimeSpan.FromSeconds(5));
                var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
                failure.Which.IsFatal.Should().BeTrue();
                failure.Which.Message.Should().Contain("429");
            }

            rateLimited.IsDisposed.Should().BeTrue();
            requests
                .Should()
                .Be(2, "a user-token 429 must not be retried or followed by queued requests");
            delays.Should().Be(0);
            client.GetRequestStats().RequestCount.Should().Be(2);
            client.GetRequestStats().HardRateLimitCount.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task User_rate_limit_blocks_the_same_token_across_new_clients()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        var initialRequests = 0;
        using var initialHttp = new HttpClient(
            new CallbackHandler(
                (request, _) =>
                {
                    Interlocked.Increment(ref initialRequests);
                    var response = request.RequestUri!.AbsolutePath.EndsWith("/users/@me")
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    return Task.FromResult(response);
                }
            )
        );

        try
        {
            using (
                var client = new DiscordClient(
                    "test-token",
                    RateLimitPreference.IgnoreAll,
                    initialHttp,
                    userTokenRequestStopStore: stopStore
                )
            )
            {
                var initialAct = async () => await client.TryGetChannelAsync(new Snowflake(1));
                (await initialAct.Should().ThrowAsync<DiscordChatExporterException>())
                    .Which.Message.Should()
                    .Contain("429");
            }

            Directory.GetFiles(stopDirectory).Should().ContainSingle();
            initialRequests.Should().Be(2);

            var restartedRequests = 0;
            using var restartedHttp = new HttpClient(
                new CallbackHandler(
                    (_, _) =>
                    {
                        Interlocked.Increment(ref restartedRequests);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                    }
                )
            );
            using var restartedClient = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                restartedHttp,
                userTokenRequestStopStore: new UserTokenRequestStopStore(stopDirectory)
            );

            var restartedAct = async () =>
                await restartedClient.TryGetChannelAsync(new Snowflake(1));
            (await restartedAct.Should().ThrowAsync<DiscordChatExporterException>())
                .Which.Message.Should()
                .Contain("HTTP 429")
                .And.Contain("future runs of this fork");
            restartedRequests.Should().Be(0);

            var differentTokenRequests = 0;
            using var differentTokenHttp = new HttpClient(
                new CallbackHandler(
                    (request, _) =>
                    {
                        Interlocked.Increment(ref differentTokenRequests);
                        var response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("[]"),
                        };
                        return Task.FromResult(response);
                    }
                )
            );
            using var differentTokenClient = new DiscordClient(
                "different-token",
                RateLimitPreference.IgnoreAll,
                differentTokenHttp,
                userTokenRequestStopStore: stopStore
            );

            var guildCount = 0;
            await foreach (var _ in differentTokenClient.GetUserGuildsAsync())
                guildCount++;
            guildCount.Should().Be(1, "the client yields the direct-messages pseudo-guild");
            differentTokenRequests.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task User_probe_rate_limit_does_not_persist_stop_or_block_bot_fallback()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        using var probeHttp = new HttpClient(
            new CallbackHandler(
                (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))
            )
        );

        try
        {
            using (
                var probeClient = new DiscordClient(
                    "test-token",
                    RateLimitPreference.IgnoreAll,
                    probeHttp,
                    userTokenRequestStopStore: stopStore
                )
            )
            {
                var probeAct = async () => await probeClient.TryGetChannelAsync(new Snowflake(1));
                (await probeAct.Should().ThrowAsync<DiscordChatExporterException>())
                    .Which.Message.Should()
                    .Contain("token as a user token");
            }

            Directory.Exists(stopDirectory).Should().BeFalse();

            var fallbackRequests = 0;
            using var fallbackHttp = new HttpClient(
                new CallbackHandler(
                    (request, _) =>
                    {
                        Interlocked.Increment(ref fallbackRequests);
                        if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                        {
                            var isBot = request
                                .Headers.GetValues("Authorization")
                                .Single()
                                .StartsWith("Bot ", StringComparison.Ordinal);
                            return Task.FromResult(
                                new HttpResponseMessage(
                                    isBot ? HttpStatusCode.OK : HttpStatusCode.Unauthorized
                                )
                            );
                        }

                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }
                )
            );
            using var fallbackClient = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                fallbackHttp,
                userTokenRequestStopStore: new UserTokenRequestStopStore(stopDirectory)
            );

            (await fallbackClient.TryGetChannelAsync(new Snowflake(1))).Should().BeNull();
            fallbackRequests
                .Should()
                .Be(3, "the fresh client should probe user, probe bot, then fetch the channel");
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task User_rate_limit_still_stops_when_persistent_marker_cannot_be_written()
    {
        var stopPath = Path.Combine(Path.GetTempPath(), $"dce-user-token-stop-{Guid.NewGuid():N}");
        File.WriteAllText(stopPath, "not a directory");
        var requests = 0;
        using var http = new HttpClient(
            new CallbackHandler(
                (request, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(
                        new HttpResponseMessage(
                            request.RequestUri!.AbsolutePath.EndsWith("/users/@me")
                                ? HttpStatusCode.OK
                                : HttpStatusCode.TooManyRequests
                        )
                    );
                }
            )
        );

        try
        {
            using var client = new DiscordClient(
                "test-token",
                RateLimitPreference.IgnoreAll,
                http,
                userTokenRequestStopStore: new UserTokenRequestStopStore(stopPath)
            );

            for (ulong id = 1; id <= 2; id++)
            {
                var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
                (await act.Should().ThrowAsync<DiscordChatExporterException>())
                    .Which.Message.Should()
                    .Contain("local stop could not be saved");
            }

            requests.Should().Be(2, "a failed marker write must still latch the in-process stop");
        }
        finally
        {
            if (File.Exists(stopPath))
                File.Delete(stopPath);
        }
    }

    [Fact]
    public async Task Verification_required_during_probe_stops_without_bot_fallback()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":40002}") },
            ])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        for (ulong id = 1; id <= 2; id++)
        {
            var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
            (await act.Should().ThrowAsync<DiscordChatExporterException>())
                .Which.IsFatal.Should()
                .BeTrue();
        }
        client.GetRequestStats().RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Challenge_response_records_a_persistent_user_token_stop()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-user-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        var challenge = new TrackingHttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<html><body>Just a moment...</body></html>",
                System.Text.Encoding.UTF8,
                "text/html"
            ),
        };
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), challenge])
        );

        try
        {
            using var client = new DiscordClient(
                "challenge-token",
                RateLimitPreference.IgnoreAll,
                http,
                userTokenRequestStopStore: stopStore
            );
            for (ulong id = 1; id <= 2; id++)
            {
                var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
                var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
                failure.Which.IsFatal.Should().BeTrue();
                failure.Which.Message.Should().Contain("challenge").And.NotContain("Just a moment");
            }
            client.GetRequestStats().RequestCount.Should().Be(2);
            stopStore.TryGetStopReason("challenge-token", out var recordedReason).Should().BeTrue();
            recordedReason.Should().Contain("bot mitigation");
        }
        finally
        {
            if (Directory.Exists(stopDirectory))
                Directory.Delete(stopDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Challenge_response_for_a_bot_token_does_not_record_a_user_token_stop()
    {
        var stopDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dce-bot-token-stop-{Guid.NewGuid():N}"
        );
        var stopStore = new UserTokenRequestStopStore(stopDirectory);
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.Unauthorized),
                new(HttpStatusCode.OK),
                new(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "<html><body>Just a moment...</body></html>",
                        System.Text.Encoding.UTF8,
                        "text/html"
                    ),
                },
            ])
        );
        using var client = new DiscordClient(
            "bot-token",
            RateLimitPreference.IgnoreAll,
            http,
            userTokenRequestStopStore: stopStore
        );

        var act = async () => await client.TryGetChannelAsync(new Snowflake(1));
        (await act.Should().ThrowAsync<DiscordChatExporterException>())
            .Which.IsFatal.Should()
            .BeTrue();
        stopStore.TryGetStopReason("bot-token", out _).Should().BeFalse();

        if (Directory.Exists(stopDirectory))
            Directory.Delete(stopDirectory, recursive: true);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }
}
