using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public partial class RateLimitSpecs
{
    [Fact]
    public async Task Concurrent_cooldown_extension_is_honored_by_existing_waiters()
    {
        static TaskCompletionSource Signal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = Signal();
        var secondEntered = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();
        var firstWait = Signal();
        var shortWaitsEntered = Signal();
        var longWaitsEntered = Signal();
        var releaseShort = Signal();
        var releaseLong = Signal();
        var calls = 0;
        var shortWaits = 0;
        var longWaits = 0;
        using var http = new HttpClient(
            new CallbackHandler(
                async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/users/@me"))
                        return new HttpResponseMessage(
                            request.Headers.Authorization?.Scheme == "Bot"
                                ? HttpStatusCode.OK
                                : HttpStatusCode.Unauthorized
                        );
                    var call = Interlocked.Increment(ref calls);
                    if (call == 1)
                    {
                        firstEntered.TrySetResult();
                        await releaseFirst.Task.WaitAsync(ct);
                    }
                    if (call == 2)
                    {
                        secondEntered.TrySetResult();
                        await releaseSecond.Task.WaitAsync(ct);
                    }
                    var response = new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (call <= 2)
                    {
                        response.Headers.Add("X-RateLimit-Remaining", "0");
                        response.Headers.Add("X-RateLimit-Reset-After", call == 1 ? "2" : "120");
                    }
                    return response;
                }
            )
        );
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            async (delay, ct) =>
            {
                if (delay < TimeSpan.FromSeconds(10))
                {
                    firstWait.TrySetResult();
                    if (Interlocked.Increment(ref shortWaits) == 2)
                        shortWaitsEntered.TrySetResult();
                    await releaseShort.Task.WaitAsync(ct);
                }
                else
                {
                    if (Interlocked.Increment(ref longWaits) == 3)
                        longWaitsEntered.TrySetResult();
                    await releaseLong.Task.WaitAsync(ct);
                }
            }
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = client.TryGetChannelAsync(new Snowflake(1), timeout.Token).AsTask();
        await firstEntered.Task.WaitAsync(timeout.Token);
        var second = client.TryGetChannelAsync(new Snowflake(2), timeout.Token).AsTask();
        await secondEntered.Task.WaitAsync(timeout.Token);
        releaseFirst.TrySetResult();
        await firstWait.Task.WaitAsync(timeout.Token);
        var third = client.TryGetChannelAsync(new Snowflake(3), timeout.Token).AsTask();
        try
        {
            await shortWaitsEntered.Task.WaitAsync(timeout.Token);
            releaseSecond.TrySetResult();
            // The second response must establish the extension before the shorter waits finish.
            while (Volatile.Read(ref longWaits) == 0)
                await Task.Delay(1, timeout.Token);
            releaseShort.TrySetResult();
            await longWaitsEntered.Task.WaitAsync(timeout.Token);
            calls.Should().Be(2, "the third request must wait for the extended deadline");
            first.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            releaseShort.TrySetResult();
            releaseLong.TrySetResult();
            await Task.WhenAll(first, second, third);
        }
        calls.Should().Be(3);
    }
}
