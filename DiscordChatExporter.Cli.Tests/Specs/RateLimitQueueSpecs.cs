using System;
using System.Collections.Generic;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_queued_requests_wait_for_cooldown(bool hardLimit)
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var queuedSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var channelRequests = 0;
        using var http = new HttpClient(
            new CallbackHandler(
                async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    if (Interlocked.Increment(ref channelRequests) == 1)
                    {
                        firstEntered.TrySetResult();
                        await releaseResponse.Task.WaitAsync(ct);
                        if (hardLimit)
                            return CreateRateLimitResponse(TimeSpan.FromSeconds(2));
                        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
                        response.Headers.Add("X-RateLimit-Remaining", "0");
                        response.Headers.Add("X-RateLimit-Reset-After", "2");
                        return response;
                    }
                    queuedSent.TrySetResult();
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            )
        );
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            async (_, ct) =>
            {
                delayEntered.TrySetResult();
                await releaseDelay.Task.WaitAsync(ct);
            }
        );
        var first = client.TryGetChannelAsync(new Snowflake(1)).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.TryGetChannelAsync(new Snowflake(2)).AsTask();
        releaseResponse.TrySetResult();
        try
        {
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(queuedSent.Task, Task.Delay(150));
            queuedSent
                .Task.IsCompleted.Should()
                .BeFalse("a queued request must honor the cooldown");
        }
        finally
        {
            releaseDelay.TrySetResult();
            await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Cancelling_a_retry_releases_the_slot_but_preserves_the_cooldown()
    {
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var waits = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                CreateRateLimitResponse(TimeSpan.FromSeconds(120)),
                new(HttpStatusCode.NotFound),
            ])
        );
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            async (delay, ct) =>
            {
                if (Interlocked.Increment(ref waits) == 1)
                {
                    delayEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                else
                {
                    delay.Should().BeGreaterThan(TimeSpan.FromSeconds(60));
                }
            }
        );
        var first = client.TryGetChannelAsync(new Snowflake(1), cancellation.Token).AsTask();
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var act = async () => await first;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await client
            .TryGetChannelAsync(new Snowflake(2))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        waits.Should().Be(2);
        client.GetRequestStats().RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task Advisory_wait_is_not_shortened_to_sixty_seconds()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset-After", "120");
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), response])
        );
        var delays = new List<TimeSpan>();
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            }
        );
        await client.TryGetChannelAsync(new Snowflake(1));
        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(121));
    }

    [Fact]
    public async Task Optional_message_count_does_not_hide_account_restrictions()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":40002}") },
            ])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        var channel = new Channel(
            new Snowflake(1),
            ChannelKind.GuildTextChat,
            new Snowflake(2),
            null,
            "test",
            null,
            null,
            null,
            false,
            null
        );
        var act = async () => await client.CountMessagesAsync(channel);
        (await act.Should().ThrowAsync<DiscordChatExporterException>())
            .Which.IsFatal.Should()
            .BeTrue();
    }
}
