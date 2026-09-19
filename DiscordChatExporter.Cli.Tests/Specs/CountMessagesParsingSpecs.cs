using System.Net;
using System.Text.Json;
using DiscordChatExporter.Core.Discord;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CountMessagesParsingSpecs
{
    [Fact]
    public void Parses_total_results_from_successful_search_response()
    {
        using var document = JsonDocument.Parse("""{"total_results":4}""");

        var result = DiscordClient.TryParseMessageSearchTotal(
            HttpStatusCode.OK,
            document.RootElement
        );

        result.Should().Be(4);
    }

    [Fact]
    public void Returns_null_for_accepted_search_response()
    {
        using var document = JsonDocument.Parse("""{"retry_after":1}""");

        var result = DiscordClient.TryParseMessageSearchTotal(
            HttpStatusCode.Accepted,
            document.RootElement
        );

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_total_results_is_missing()
    {
        using var document = JsonDocument.Parse("""{"messages":[]}""");

        var result = DiscordClient.TryParseMessageSearchTotal(
            HttpStatusCode.OK,
            document.RootElement
        );

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_total_results_is_not_a_number()
    {
        using var document = JsonDocument.Parse("""{"total_results":"4"}""");

        var result = DiscordClient.TryParseMessageSearchTotal(
            HttpStatusCode.OK,
            document.RootElement
        );

        result.Should().BeNull();
    }
}
