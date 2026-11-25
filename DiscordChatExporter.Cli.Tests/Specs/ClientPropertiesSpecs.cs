using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Utils;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ClientPropertiesSpecs
{
    [Fact]
    public async Task Shared_http_client_rejects_outbound_requests()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://discord.com/api/v10/users/@me"
        );

        var act = async () => await Http.Client.SendAsync(request);

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .WithMessage("Discord network access is disabled in this build.");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public async Task Cli_diagnostics_use_stderr_and_survive_failure_and_cancellation(
        int outcome,
        bool diagnostics
    )
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, output, error);
        using var command = new DiagnosticTestCommand(outcome)
        {
            Token = "private-token",
            ShowDiagnostics = diagnostics,
        };

        var act = async () => await command.ExecuteAsync(console);
        if (outcome == 1)
            await act.Should().ThrowAsync<InvalidOperationException>();
        else if (outcome == 2)
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
            await act.Should().NotThrowAsync();

        console.Error.Flush();
        console.Output.Flush();
        var text = Encoding.UTF8.GetString(error.ToArray());
        text.Split("Core version:").Length.Should().Be(diagnostics ? 3 : 1);
        text.Should().NotContain("private-token");
        Encoding.UTF8.GetString(output.ToArray()).Should().NotContain("Core version:");
    }

    private sealed class DiagnosticTestCommand(int outcome) : DiscordCommandBase, IDisposable
    {
        protected override ValueTask ExecuteCoreAsync(IConsole console) =>
            outcome switch
            {
                1 => throw new InvalidOperationException("Test failure"),
                2 => throw new OperationCanceledException(),
                _ => ValueTask.CompletedTask,
            };

        public void Dispose() => Discord.Dispose();
    }
}
