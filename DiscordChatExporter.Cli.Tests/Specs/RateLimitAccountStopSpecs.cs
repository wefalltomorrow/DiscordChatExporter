using System;
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
        using var client = new DiscordClient("private-token", RateLimitPreference.IgnoreAll, http);
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
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(HttpStatusCode.Unauthorized)])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
        for (ulong id = 1; id <= 2; id++)
        {
            var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
            (await act.Should().ThrowAsync<DiscordChatExporterException>())
                .Which.IsFatal.Should()
                .BeTrue();
        }
        client.GetRequestStats().RequestCount.Should().Be(2);
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
        using var client = new DiscordClient("test-token", RateLimitPreference.IgnoreAll, http);
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
