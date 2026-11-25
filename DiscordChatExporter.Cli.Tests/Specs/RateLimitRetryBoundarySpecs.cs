using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public partial class RateLimitSpecs
{
    [Fact]
    public async Task Exhausted_rate_limit_retries_stop_later_operations()
    {
        var responses = new List<HttpResponseMessage> { new(HttpStatusCode.OK) };
        for (var i = 0; i < 9; i++)
            responses.Add(CreateRateLimitResponse(TimeSpan.Zero));
        // Would be sent by the next channel if the terminal 429 is not latched.
        responses.Add(new(HttpStatusCode.NotFound));
        using var http = new HttpClient(new QueueHttpMessageHandler(responses));
        using var client = new DiscordClient(
            "test-token",
            RateLimitPreference.RespectAll,
            http,
            (_, _) => ValueTask.CompletedTask
        );
        for (ulong id = 1; id <= 2; id++)
        {
            var act = async () => await client.TryGetChannelAsync(new Snowflake(id));
            var failure = await act.Should().ThrowAsync<DiscordChatExporterException>();
            failure.Which.IsFatal.Should().BeTrue();
        }
        client.GetRequestStats().RequestCount.Should().Be(10);
        client.GetRequestStats().HardRateLimitCount.Should().Be(9);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task Non_object_rate_limit_payload_uses_valid_header(string body)
    {
        var response = CreateRateLimitResponse(TimeSpan.FromSeconds(12));
        response.Content = new StringContent(body);
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                response,
                new(HttpStatusCode.NotFound),
            ])
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
        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public async Task Retry_after_http_date_is_honored()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddMinutes(2)
        );
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                response,
                new(HttpStatusCode.NotFound),
            ])
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
        delays.Should().ContainSingle().Which.Should().BeGreaterThan(TimeSpan.FromSeconds(110));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_returns_non_retryable_exit_code_for_direct_or_wrapped_stop(bool wrap)
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([
                new(HttpStatusCode.OK),
                new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":40002}") },
            ])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.RespectAll, http);
        var command = new StopTestCommand(client, wrap) { Token = "test-token" };
        using var console = new FakeConsole(Stream.Null, new MemoryStream(), new MemoryStream());
        var act = async () => await command.ExecuteAsync(console);
        var failure = await act.Should().ThrowAsync<CommandException>();
        failure.Which.ExitCode.Should().Be(3);
        failure.Which.Message.Should().Contain("40002");

        var app = new CommandLineApplicationBuilder()
            .AddCommand(new CommandDescriptor<StopTestCommand>(null, null, []))
            .UseTypeInstantiator(_ => command)
            .UseConsole(console)
            .Build();
        (await app.RunAsync([], new Dictionary<string, string>())).Should().Be(3);
    }

    [Fact]
    public async Task Cli_preserves_ordinary_failure_exit_code()
    {
        using var http = new HttpClient(
            new QueueHttpMessageHandler([new(HttpStatusCode.OK), new(HttpStatusCode.BadRequest)])
        );
        using var client = new DiscordClient("test-token", RateLimitPreference.RespectAll, http);
        var command = new StopTestCommand(client, false) { Token = "test-token" };
        using var console = new FakeConsole(Stream.Null, new MemoryStream(), new MemoryStream());
        var app = new CommandLineApplicationBuilder()
            .AddCommand(new CommandDescriptor<StopTestCommand>(null, null, []))
            .UseTypeInstantiator(_ => command)
            .UseConsole(console)
            .Build();
        (await app.RunAsync([], new Dictionary<string, string>())).Should().Be(1);
        client.RequestStopReason.Should().BeNull();
    }

    private sealed class StopTestCommand(DiscordClient client, bool wrap) : DiscordCommandBase
    {
        protected override DiscordClient CreateDiscordClient() => client;

        protected override async ValueTask ExecuteCoreAsync(IConsole console)
        {
            try
            {
                await Discord.TryGetChannelAsync(new Snowflake(1));
            }
            catch (Exception ex) when (wrap)
            {
                throw new DiscordChatExporterException("Export failed.", true, ex);
            }
        }
    }
}
