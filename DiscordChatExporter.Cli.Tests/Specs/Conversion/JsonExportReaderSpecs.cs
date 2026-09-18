using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class JsonExportReaderSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceJsonReader_" + Guid.NewGuid().ToString("N")
    );

    public JsonExportReaderSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ExportContext CreateContext(string outputPath, bool shouldFormatMarkdown = false)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            "topic",
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, User mention) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
            null,
            null,
            false,
            "the quick brown fox",
            [
                new Attachment(
                    new Snowflake(50),
                    "https://cdn.example/file.png",
                    "file.png",
                    null,
                    null,
                    null,
                    FileSize.FromBytes(123)
                ),
            ],
            [
                new Embed(
                    "embed title",
                    EmbedKind.Rich,
                    null,
                    null,
                    null,
                    null,
                    "embed body",
                    [],
                    null,
                    [],
                    null,
                    null
                ),
            ],
            [],
            [],
            [mention],
            null,
            null,
            null,
            null
        );

    private async Task<string> WriteJsonAsync(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        var author = CreateUser(10, "alice");
        var mention = CreateUser(11, "bob");
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        await writer.WriteMessageAsync(CreateMessage(1001, author, mention));
        await writer.WriteMessageAsync(
            CreateMessage(1002, author, mention) with
            {
                Content = "second message",
            }
        );
        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteRawJsonAsync(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task Reader_prefers_raw_embed_markdown_fields()
    {
        var path = Path.Combine(_dir, "embed-raw-markdown.json");
        var author = CreateUser(10, "alice");
        await using (
            var writer = new JsonMessageWriter(
                File.Create(path),
                CreateContext(path, shouldFormatMarkdown: true)
            )
        )
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(1001, author, CreateUser(11, "bob")) with
                {
                    Embeds =
                    [
                        new Embed(
                            "**raw title**",
                            EmbedKind.Rich,
                            null,
                            null,
                            null,
                            null,
                            "__raw description__",
                            [new EmbedField("**field name**", "`field value`", false)],
                            null,
                            [],
                            null,
                            null
                        ),
                    ],
                }
            );
            await writer.WritePostambleAsync();
        }

        var parsed = await JsonExportReader.ParseAsync(path);

        var embed = parsed.Messages.Single().Embeds.Should().ContainSingle().Subject;
        embed.Title.Should().Be("**raw title**");
        embed.Description.Should().Be("__raw description__");
        var field = embed.Fields.Should().ContainSingle().Subject;
        field.Name.Should().Be("**field name**");
        field.Value.Should().Be("`field value`");
    }

    [Fact]
    public async Task Reader_reconstructs_messages_from_json_export()
    {
        var path = await WriteJsonAsync("chat.json");

        var parsed = await JsonExportReader.ParseAsync(path);

        parsed.Guild.Id.Should().Be(new Snowflake(1));
        parsed.Channel.Id.Should().Be(new Snowflake(2));
        parsed.Messages.Should().HaveCount(2);
        parsed.Messages[0].Content.Should().Be("the quick brown fox");
        parsed.Messages[0].Author.Id.Should().Be(new Snowflake(10));
        parsed.Messages[0].Attachments.Should().ContainSingle();
        parsed.Messages[0].Embeds.Should().ContainSingle().Which.Title.Should().Be("embed title");
        parsed
            .Messages[0]
            .MentionedUsers.Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(new Snowflake(11));
    }

    [Fact]
    public async Task Reader_treats_a_malformed_embed_color_as_null_instead_of_throwing()
    {
        var path = await WriteRawJsonAsync(
            "bad-embed-color.json",
            """
            {
              "guild": { "id": "1", "name": "Test Guild", "iconUrl": "" },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "bad color",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [
                    {
                      "title": "embed title",
                      "type": "Rich",
                      "url": null,
                      "timestamp": null,
                      "color": "not-a-color",
                      "description": "embed body",
                      "fields": [],
                      "images": [],
                      "inlineEmojis": []
                    }
                  ],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );

        var parsed = await JsonExportReader.ParseAsync(path);

        parsed.Messages.Single().Embeds.Single().Color.Should().BeNull();
    }

    [Fact]
    public async Task Reader_prefers_raw_content_and_preserves_flags_bounds_and_attachment_metadata()
    {
        var path = await WriteRawJsonAsync(
            "raw-fields.json",
            """
            {
              "guild": { "id": "1", "name": "Test Guild", "iconUrl": "" },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "dateRange": {
                "after": "2021-07-01T00:00:00+00:00",
                "before": "2021-08-01T00:00:00+00:00"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "flags": 2,
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "hello @bob",
                  "contentRaw": "hello <@11>",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [
                    {
                      "id": "50",
                      "url": "file-local.png",
                      "fileName": "file.png",
                      "description": "alt text",
                      "width": 640,
                      "height": 480,
                      "fileSizeBytes": 123
                    }
                  ],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );

        var parsed = await JsonExportReader.ParseAsync(path);
        var message = parsed.Messages.Should().ContainSingle().Subject;

        parsed
            .After.Should()
            .Be(Snowflake.FromDate(DateTimeOffset.Parse("2021-07-01T00:00:00+00:00")));
        parsed
            .Before.Should()
            .Be(Snowflake.FromDate(DateTimeOffset.Parse("2021-08-01T00:00:00+00:00")));
        message.Content.Should().Be("hello <@11>");
        message.Flags.Should().Be(MessageFlags.CrossPost);
        var attachment = message.Attachments.Should().ContainSingle().Subject;
        attachment.Description.Should().Be("alt text");
        attachment.Width.Should().Be(640);
        attachment.Height.Should().Be(480);
    }

    [Fact]
    public async Task Reader_rejects_malformed_or_non_dce_json()
    {
        var malformed = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(malformed, "{");
        var nonDce = Path.Combine(_dir, "not-dce.json");
        await File.WriteAllTextAsync(nonDce, "{\"hello\":1}");

        await FluentActions
            .Awaiting(() => JsonExportReader.ParseAsync(malformed).AsTask())
            .Should()
            .ThrowAsync<InvalidExportException>();
        await FluentActions
            .Awaiting(() => JsonExportReader.ParseAsync(nonDce).AsTask())
            .Should()
            .ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Reader_parses_conversion_data_and_tolerates_legacy_json_without_it()
    {
        var path = await WriteJsonAsync("chat.json");

        (await JsonExportReader.ParseAsync(path)).ConversionData.Should().NotBeNull();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("conversionData"))
                    continue;

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var legacyPath = Path.Combine(_dir, "legacy.json");
        await File.WriteAllBytesAsync(legacyPath, stream.ToArray());

        (await JsonExportReader.ParseAsync(legacyPath)).ConversionData.Should().BeNull();
    }

    [Fact]
    public async Task Reader_parses_standard_emoji_reactions_without_a_snowflake_id()
    {
        var path = Path.Combine(_dir, "standard-reaction.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "reacted",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [
                    {
                      "emoji": {
                        "id": "",
                        "name": "🙂",
                        "code": "slight_smile",
                        "isAnimated": false,
                        "imageUrl": "emoji-local.png"
                      },
                      "count": 3,
                      "users": []
                    }
                  ],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );

        var parsed = await JsonExportReader.ParseAsync(path);

        var reaction = parsed
            .Messages.Should()
            .ContainSingle()
            .Subject.Reactions.Should()
            .ContainSingle()
            .Subject;
        reaction.Emoji.Id.Should().BeNull();
        reaction.Emoji.Name.Should().Be("🙂");
        reaction.Emoji.ImageUrl.Should().Be("emoji-local.png");
    }

    [Fact]
    public async Task Reader_preserves_explicit_embed_kinds_from_json_export()
    {
        var path = Path.Combine(_dir, "embed-kinds.json");
        var author = CreateUser(10, "alice");
        var embeds = new[]
        {
            new Embed(
                null,
                EmbedKind.Image,
                "https://cdn.example/image.png",
                null,
                null,
                null,
                null,
                [],
                null,
                [new EmbedImage("https://cdn.example/image.png", "image-local.png", 640, 480)],
                null,
                null
            ),
            new Embed(
                null,
                EmbedKind.Video,
                "https://cdn.example/video.mp4",
                null,
                null,
                null,
                null,
                [],
                null,
                [],
                new EmbedVideo("https://cdn.example/video.mp4", "video-local.mp4", 640, 480),
                null
            ),
            new Embed(
                null,
                EmbedKind.Gifv,
                "https://cdn.example/animation.gifv",
                null,
                null,
                null,
                null,
                [],
                null,
                [],
                new EmbedVideo(
                    "https://cdn.example/animation.mp4",
                    "animation-local.mp4",
                    320,
                    240
                ),
                null
            ),
            new Embed(
                "link title",
                EmbedKind.Link,
                "https://example.com/page",
                null,
                null,
                null,
                "link body",
                [],
                null,
                [],
                null,
                null
            ),
        };

        await using (var writer = new JsonMessageWriter(File.Create(path), CreateContext(path)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(1001, author, CreateUser(11, "bob")) with
                {
                    Embeds = embeds,
                }
            );
            await writer.WritePostambleAsync();
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        document
            .RootElement.GetProperty("messages")[0]
            .GetProperty("embeds")
            .EnumerateArray()
            .Select(embed => embed.GetProperty("type").GetString())
            .Should()
            .Equal("Image", "Video", "Gifv", "Link");

        var parsed = await JsonExportReader.ParseAsync(path);

        parsed
            .Messages.Single()
            .Embeds.Select(embed => embed.Kind)
            .Should()
            .Equal(embeds.Select(e => e.Kind));
    }

    [Fact]
    public async Task Reader_infers_legacy_embed_kinds_when_type_is_missing()
    {
        var path = await WriteRawJsonAsync(
            "legacy-embed-kinds.json",
            """
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "legacy embeds",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [
                    {
                      "title": null,
                      "url": "https://cdn.example/image.png",
                      "timestamp": null,
                      "description": null,
                      "images": [
                        {
                          "url": "image-local.png",
                          "canonicalUrl": "https://cdn.example/image.png",
                          "width": 640,
                          "height": 480
                        }
                      ],
                      "fields": [],
                      "inlineEmojis": []
                    },
                    {
                      "title": null,
                      "url": "https://cdn.example/video.mp4",
                      "timestamp": null,
                      "description": null,
                      "video": {
                        "url": "video-local.mp4",
                        "canonicalUrl": "https://cdn.example/video.mp4",
                        "width": 640,
                        "height": 480
                      },
                      "images": [],
                      "fields": [],
                      "inlineEmojis": []
                    },
                    {
                      "title": "link title",
                      "url": "https://example.com/page",
                      "timestamp": null,
                      "description": "link body",
                      "images": [],
                      "fields": [],
                      "inlineEmojis": []
                    }
                  ],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );

        var parsed = await JsonExportReader.ParseAsync(path);

        parsed
            .Messages.Single()
            .Embeds.Select(embed => embed.Kind)
            .Should()
            .Equal(EmbedKind.Image, EmbedKind.Video, EmbedKind.Link);
    }

    public static IEnumerable<object[]> MalformedDceJsonCases()
    {
        yield return
        [
            "missing-guild-id.json",
            """
                {
                  "guild": {
                    "name": "Test Guild",
                    "iconUrl": ""
                  },
                  "channel": {
                    "id": "2",
                    "type": "GuildTextChat",
                    "categoryId": null,
                    "category": null,
                    "name": "test-channel",
                    "topic": "topic"
                  },
                  "messages": []
                }
                """,
        ];

        yield return
        [
            "null-message-author.json",
            """
                {
                  "guild": {
                    "id": "1",
                    "name": "Test Guild",
                    "iconUrl": ""
                  },
                  "channel": {
                    "id": "2",
                    "type": "GuildTextChat",
                    "categoryId": null,
                    "category": null,
                    "name": "test-channel",
                    "topic": "topic"
                  },
                  "messages": [
                    {
                      "id": "1001",
                      "type": "Default",
                      "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                      "timestampEdited": null,
                      "callEndedTimestamp": null,
                      "isPinned": false,
                      "content": "bad",
                      "author": null,
                      "attachments": [],
                      "embeds": [],
                      "stickers": [],
                      "reactions": [],
                      "mentions": [],
                      "inlineEmojis": []
                    }
                  ]
                }
                """,
        ];
    }

    [Theory]
    [MemberData(nameof(MalformedDceJsonCases))]
    public async Task Reader_wraps_malformed_dce_shaped_json_as_invalid_export(
        string fileName,
        string json
    )
    {
        var path = await WriteRawJsonAsync(fileName, json);

        await FluentActions
            .Awaiting(() => JsonExportReader.ParseAsync(path).AsTask())
            .Should()
            .ThrowAsync<InvalidExportException>();
    }
}
