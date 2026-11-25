using System;
using System.Net;
using System.Net.Http;
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
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("1e300", false)]
    [InlineData("-1", false)]
    [InlineData("bad", false)]
    [InlineData(null, false)]
    [InlineData("5000000", false)]
    [InlineData("NaN", true)]
    [InlineData("Infinity", true)]
    [InlineData("1e300", true)]
    [InlineData("-1", true)]
    [InlineData("bad", true)]
    [InlineData("5000000", true)]
    public async Task Unusable_rate_limit_headers_stop_without_sending_more_requests(
        string? value,
        bool hard
    )
    {
        var response = new TrackingHttpResponseMessage(
            hard ? HttpStatusCode.TooManyRequests : HttpStatusCode.NotFound
        );
        response.Headers.Add("X-RateLimit-Remaining", "0");
        if (value is not null)
            response.Headers.Add("X-RateLimit-Reset-After", value);
        await AssertUnusableTimingStopsAsync(response);
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("-1")]
    [InlineData("\"NaN\"")]
    [InlineData("\"bad\"")]
    [InlineData("null")]
    [InlineData("5000000")]
    public async Task Unusable_retry_after_body_stops_instead_of_short_retry(string value)
    {
        var response = new TrackingHttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"retry_after\":" + value + "}"),
        };
        await AssertUnusableTimingStopsAsync(response);
    }

    [Fact]
    public async Task Retry_after_beyond_supported_timer_range_stops()
    {
        var response = new TrackingHttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(System.DateTimeOffset.UtcNow.AddYears(1));
        await AssertUnusableTimingStopsAsync(response);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("5000000")]
    public async Task Unusable_standard_retry_header_stops(string value)
    {
        var response = new TrackingHttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        await AssertUnusableTimingStopsAsync(response);
    }

    private static async Task AssertUnusableTimingStopsAsync(TrackingHttpResponseMessage response)
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                response,
                new(HttpStatusCode.NotFound),
            ])
        );
        var waits = 0;
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            (_, _) =>
            {
                waits++;
                return ValueTask.CompletedTask;
            }
        );
        for (ulong id = 1; id <= 2; id++)
        {
            var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
            var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
            failure.Which.IsFatal.Should().BeTrue();
            failure.Which.Message.Should().Contain("unusable rate-limit duration");
        }
        response.IsDisposed.Should().BeTrue();
        waits.Should().Be(0);
        client.GetRequestStats().RequestCount.Should().Be(2);
    }
}
