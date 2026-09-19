using System;
using System.Linq;
using System.Text.Json;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class DiscordDataParsingSpecs
{
    [Fact]
    public void Snowflake_from_date_clamps_dates_before_discord_epoch()
    {
        Snowflake
            .FromDate(new DateTimeOffset(2014, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .Should()
            .Be(Snowflake.Zero);
    }

    [Fact]
    public void Message_parser_reads_current_interaction_metadata()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": "1000",
              "type": 0,
              "flags": 0,
              "author": {
                "id": "10",
                "username": "alice",
                "global_name": "Alice",
                "discriminator": "0",
                "avatar": null
              },
              "timestamp": "2026-01-01T00:00:00+00:00",
              "content": "/hello",
              "attachments": [],
              "embeds": [],
              "mentions": [],
              "interaction_metadata": {
                "id": "20",
                "user": {
                  "id": "11",
                  "username": "bob",
                  "global_name": "Bob",
                  "discriminator": "0",
                  "avatar": null
                }
              }
            }
            """
        );

        var message = Message.Parse(document.RootElement);

        message.Interaction.Should().NotBeNull();
        message.Interaction!.Name.Should().Be("");
    }

    [Fact]
    public void Sticker_parser_tolerates_unknown_future_format_types()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": "1000",
              "name": "future",
              "format_type": 999
            }
            """
        );

        var sticker = Sticker.Parse(document.RootElement);

        sticker.SourceUrl.Should().EndWith(".png");
    }

    [Fact]
    public void Message_parser_merges_x_com_multi_image_embeds()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": "1000",
              "type": 0,
              "flags": 0,
              "author": {
                "id": "10",
                "username": "alice",
                "global_name": "Alice",
                "discriminator": "0",
                "avatar": null
              },
              "timestamp": "2026-01-01T00:00:00+00:00",
              "content": "",
              "attachments": [],
              "embeds": [
                {
                  "type": "rich",
                  "url": "https://x.com/alice/status/123",
                  "title": "post",
                  "image": { "url": "https://img.example/1.jpg" }
                },
                {
                  "type": "rich",
                  "url": "https://x.com/alice/status/123",
                  "image": { "url": "https://img.example/2.jpg" }
                },
                {
                  "type": "rich",
                  "url": "https://x.com/alice/status/123",
                  "image": { "url": "https://img.example/3.jpg" }
                }
              ],
              "mentions": []
            }
            """
        );

        var message = Message.Parse(document.RootElement);

        message.Embeds.Should().ContainSingle();
        message
            .Embeds.Single()
            .Images.Select(i => i.Url)
            .Should()
            .Equal(
                "https://img.example/1.jpg",
                "https://img.example/2.jpg",
                "https://img.example/3.jpg"
            );
    }
}
