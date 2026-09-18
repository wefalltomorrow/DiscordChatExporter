using System;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Filtering;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class MessageFilterFactorySpecs
{
    private static Message CreateMessage(User author, string content = "hello") =>
        new(
            Snowflake.Zero,
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            content,
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null
        );

    [Fact]
    public void I_can_create_an_author_filter_from_a_display_name_with_spaces()
    {
        // Arrange
        var matchingMessage = CreateMessage(
            new User(new Snowflake(123), false, null, "josh", "Josh Smith", "")
        );

        var nonMatchingMessage = CreateMessage(
            new User(new Snowflake(456), false, null, "alex", "Alex Smith", "")
        );

        // Act
        var filter = MessageFilter.FromAuthor("Josh Smith");

        // Assert
        filter.IsMatch(matchingMessage).Should().BeTrue();
        filter.IsMatch(nonMatchingMessage).Should().BeFalse();
    }

    [Fact]
    public void I_can_create_a_link_filter()
    {
        // Arrange
        var matchingMessage = CreateMessage(
            new User(new Snowflake(123), false, null, "josh", "Josh", ""),
            "[docs](https://example.com/docs)"
        );

        var nonMatchingMessage = CreateMessage(
            new User(new Snowflake(456), false, null, "alex", "Alex", ""),
            "`https://example.com/code`"
        );

        // Act
        var filter = MessageFilter.Parse("has:link");

        // Assert
        filter.IsMatch(matchingMessage).Should().BeTrue();
        filter.IsMatch(nonMatchingMessage).Should().BeFalse();
    }

    [Fact]
    public void I_can_create_an_invite_filter()
    {
        // Arrange
        var matchingMessage = CreateMessage(
            new User(new Snowflake(123), false, null, "josh", "Josh", ""),
            "Join https://discord.gg/example"
        );

        var nonMatchingMessage = CreateMessage(
            new User(new Snowflake(456), false, null, "alex", "Alex", ""),
            "Visit https://example.com"
        );

        // Act
        var filter = MessageFilter.Parse("has:invite");

        // Assert
        filter.IsMatch(matchingMessage).Should().BeTrue();
        filter.IsMatch(nonMatchingMessage).Should().BeFalse();
    }
}
