using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using DiscordChatExporter.Cli.Tests.Utils;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class ExportConverterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceConverter_" + Guid.NewGuid().ToString("N")
    );

    public ExportConverterSpecs() => Directory.CreateDirectory(_dir);

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

    private static ExportContext CreateContext(string outputPath)
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
            null,
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
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
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

    private async Task<string> WriteJsonAsync()
    {
        var path = Path.Combine(_dir, "chat.json");
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        await writer.WriteMessageAsync(CreateMessage(1001, author, "the quick brown fox"));
        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteJsonAsync(string fileName, params Message[] messages)
    {
        var path = Path.Combine(_dir, fileName);
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        foreach (var message in messages)
            await writer.WriteMessageAsync(message);

        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteRawJsonAsync(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> WriteRawJsonWithMessageAsync(
        string fileName,
        string timestamp,
        string reactions = "[]"
    ) =>
        await WriteRawJsonAsync(
            fileName,
            $$"""
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
                "topic": null
              },
              "dateRange": {
                "after": null,
                "before": null
              },
              "exportedAt": "1970-01-01T00:00:00.0000000+00:00",
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "{{timestamp}}",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "hello",
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
                  "reactions": {{reactions}},
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1
            }
            """
        );

    private async Task<string> WriteLegacyMentionJsonAsync()
    {
        var path = Path.Combine(_dir, "legacy-mention.json");
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var mention = new User(
            new Snowflake(11),
            false,
            null,
            "bob",
            "Bob Display",
            "avatar-local.png"
        );

        await using (var writer = new JsonMessageWriter(File.Create(path), CreateContext(path)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(1001, author, "hello <@11> <#30>") with
                {
                    MentionedUsers = [mention],
                }
            );
            await writer.WritePostambleAsync();
        }

        var json = await File.ReadAllTextAsync(path);
        var conversionDataStart = json.IndexOf(
            ",\r\n  \"conversionData\"",
            StringComparison.Ordinal
        );
        if (conversionDataStart < 0)
            conversionDataStart = json.IndexOf(",\n  \"conversionData\"", StringComparison.Ordinal);

        await File.WriteAllTextAsync(path, json[..conversionDataStart] + "\n}");
        return path;
    }

    private async Task<string> WriteJsonWithLocalAvatarAndRemoteConversionDataAsync()
    {
        var path = await WriteLegacyMentionJsonAsync();
        var json = await File.ReadAllTextAsync(path);
        json = json.Replace(
            """
                  "avatarUrl": ""
            """,
            """
                  "avatarUrl": "avatar-local.png"
            """,
            StringComparison.Ordinal
        );
        await File.WriteAllTextAsync(
            path,
            json.TrimEnd('}', '\r', '\n')
                + """
                ,
                  "conversionData": {
                    "schemaVersion": 1,
                    "members": [
                      {
                        "id": "10",
                        "displayName": "alice",
                        "avatarUrl": "https://cdn.example/remote-avatar.png",
                        "colorHex": null,
                        "roleIds": []
                      }
                    ],
                    "roles": [],
                    "channels": []
                  }
                }
                """
        );
        return path;
    }

    [Fact]
    public async Task Converter_writes_sqlite_and_csv_outputs_from_json()
    {
        var jsonPath = await WriteJsonAsync();
        var dbOut = Path.Combine(_dir, "out.db");
        var csvOut = Path.Combine(_dir, "out.csv");

        await ExportConverter.ConvertAsync(jsonPath, dbOut, ExportFormat.Db);
        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        (await SqliteExportReader.SearchAsync(dbOut, "quick", 50, default))
            .Should()
            .ContainSingle();
        (await File.ReadAllTextAsync(csvOut)).Should().Contain("quick brown fox");
    }

    [Fact]
    public async Task Converter_preserves_source_timestamp_offset_in_csv()
    {
        var jsonPath = await WriteRawJsonWithMessageAsync(
            "offset.json",
            "2026-06-08T12:30:00.0000000-05:00"
        );
        var csvOut = Path.Combine(_dir, "offset.csv");

        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        (await File.ReadAllTextAsync(csvOut))
            .Should()
            .Contain("\"2026-06-08T12:30:00.0000000-05:00\"");
    }

    [Fact]
    public async Task Converter_reaction_counts_do_not_use_ambient_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var jsonPath = await WriteRawJsonWithMessageAsync(
                "reaction-count.json",
                "2026-06-08T12:30:00.0000000+00:00",
                """
                [
                  {
                    "emoji": {
                      "id": null,
                      "name": "wave",
                      "isAnimated": false
                    },
                    "count": 1234
                  }
                ]
                """
            );
            var csvOut = Path.Combine(_dir, "reaction-count.csv");
            var txtOut = Path.Combine(_dir, "reaction-count.txt");

            await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);
            await ExportConverter.ConvertAsync(jsonPath, txtOut, ExportFormat.PlainText);

            (await File.ReadAllTextAsync(csvOut)).Should().Contain("wave (1,234)");
            (await File.ReadAllTextAsync(txtOut)).Should().Contain("wave (1,234)");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task Converter_formats_legacy_mentions_offline_from_embedded_message_data()
    {
        var jsonPath = await WriteLegacyMentionJsonAsync();
        var csvOut = Path.Combine(_dir, "legacy.csv");

        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        var csv = await File.ReadAllTextAsync(csvOut);
        csv.Should().Contain("@Bob Display");
        csv.Should().Contain("#deleted-channel");
    }

    [Fact]
    public async Task Converter_preserves_message_avatar_url_over_conversion_data_avatar_url()
    {
        var jsonPath = await WriteJsonWithLocalAvatarAndRemoteConversionDataAsync();
        var htmlOut = Path.Combine(_dir, "avatar.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var html = await File.ReadAllTextAsync(htmlOut);
        html.Should().Contain("avatar-local.png");
        html.Should().NotContain("remote-avatar.png");
    }

    [Fact]
    public async Task Converter_converts_html_with_invite_links_offline_without_fetching_invites()
    {
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var jsonPath = await WriteJsonAsync(
            "offline-invite.json",
            CreateMessage(1001, author, "join https://discord.gg/offline")
        );
        var htmlOut = Path.Combine(_dir, "offline-invite.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        document.Body?.TextContent.Should().Contain("https://discord.gg/offline");
        document.QuerySelector(".chatlog__embed-invite-container").Should().BeNull();
    }

    [Fact]
    public async Task Converter_relinks_replies_to_messages_in_the_same_json_export()
    {
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var original = CreateMessage(1001, author, "original body");
        var reply = CreateMessage(1002, author, "reply body") with
        {
            Kind = MessageKind.Reply,
            Reference = new MessageReference(
                MessageReferenceKind.Default,
                original.Id,
                new Snowflake(2),
                new Snowflake(1)
            ),
        };
        var jsonPath = await WriteJsonAsync("reply-relink.json", original, reply);
        var htmlOut = Path.Combine(_dir, "reply-relink.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        var replyElement = document.QuerySelector("""[data-message-id="1002"]""");
        replyElement.Should().NotBeNull();
        replyElement!.QuerySelector(".chatlog__reply-unknown").Should().BeNull();
        replyElement
            .QuerySelector(".chatlog__reply-link")
            ?.Text()
            .Should()
            .Contain("original body");
    }

    [Fact]
    public async Task Converter_preserves_inline_emoji_image_url_in_html()
    {
        var jsonPath = await WriteRawJsonAsync(
            "inline-emoji-url.json",
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
                  "content": "local emoji <:local:12345>",
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
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": [
                    {
                      "id": "12345",
                      "name": "local",
                      "code": "local",
                      "isAnimated": false,
                      "imageUrl": "emoji-local.png"
                    }
                  ]
                }
              ],
              "messageCount": 1
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "inline-emoji-url.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        document
            .QuerySelectorAll(".chatlog__emoji")
            .Select(e => e.GetAttribute("src"))
            .Should()
            .Contain("emoji-local.png");
    }

    [Fact]
    public async Task Converter_preserves_legacy_singular_embed_image()
    {
        var jsonPath = await WriteRawJsonAsync(
            "legacy-singular-image.json",
            """
            {
              "guild": { "id": "1", "name": "Test Guild", "iconUrl": "" },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": null
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "",
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
                      "title": "legacy image",
                      "type": "Image",
                      "url": "https://example.com/post",
                      "image": {
                        "url": "legacy-image.png",
                        "width": 640,
                        "height": 480
                      },
                      "fields": []
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
        var htmlOut = Path.Combine(_dir, "legacy-singular-image.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        (await File.ReadAllTextAsync(htmlOut)).Should().Contain("legacy-image.png");
    }

    [Fact]
    public async Task Converter_rejects_future_conversion_data_schema_versions()
    {
        var jsonPath = await WriteRawJsonAsync(
            "future-conversion-schema.json",
            """
            {
              "guild": { "id": "1", "name": "Test Guild", "iconUrl": "" },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": null
              },
              "messages": [],
              "messageCount": 0,
              "conversionData": {
                "schemaVersion": 999,
                "members": [],
                "roles": [],
                "channels": [],
                "emojis": []
              }
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "future-conversion-schema.html");

        await FluentActions
            .Awaiting(() =>
                ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark).AsTask()
            )
            .Should()
            .ThrowAsync<InvalidExportException>()
            .WithMessage("*conversionData.schemaVersion*");
    }

    [Fact]
    public async Task Converter_preserves_legacy_author_role_color_without_conversion_data()
    {
        var jsonPath = await WriteRawJsonAsync(
            "legacy-author-color.json",
            """
            {
              "guild": { "id": "1", "name": "Test Guild", "iconUrl": "" },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": null
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "colored author",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": "#ff0000",
                    "isBot": false,
                    "roles": [
                      {
                        "id": "30",
                        "name": "red",
                        "color": "#ff0000",
                        "position": 1
                      }
                    ],
                    "avatarUrl": ""
                  },
                  "attachments": [],
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
        var htmlOut = Path.Combine(_dir, "legacy-author-color.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        document
            .QuerySelector(".chatlog__author")
            ?.GetAttribute("style")
            .Should()
            .Contain("rgb(255,0,0)");
    }

    [Fact]
    public async Task Converter_does_not_emit_local_asset_urls_from_json()
    {
        var jsonPath = await WriteRawJsonAsync(
            "unsafe-asset-urls.json",
            """
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": "file:///C:/Users/ExampleUser/secret-guild.png"
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "iconUrl": "C:/Users/ExampleUser/secret-channel.png",
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
                  "content": "unsafe assets <:local:12345>",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": "file:///C:/Users/ExampleUser/secret-avatar.png"
                  },
                  "attachments": [
                    {
                      "id": "20",
                      "url": "C:/Users/ExampleUser/secret-attachment.png",
                      "fileName": "secret-attachment.png",
                      "description": null,
                      "width": 128,
                      "height": 128,
                      "fileSizeBytes": 1234
                    }
                  ],
                  "embeds": [
                    {
                      "title": "embed",
                      "type": "Rich",
                      "url": "https://example.com/page",
                      "color": null,
                      "description": "embed body",
                      "thumbnail": {
                        "url": "file:///C:/Users/ExampleUser/secret-thumb.png",
                        "canonicalUrl": "file:///C:/Users/ExampleUser/secret-thumb-canonical.png",
                        "width": 64,
                        "height": 64
                      },
                      "images": [
                        {
                          "url": "//server/share/secret-image.png",
                          "canonicalUrl": "/etc/secret-image.png",
                          "width": 64,
                          "height": 64
                        }
                      ],
                      "footer": {
                        "text": "footer",
                        "iconUrl": "file:///C:/Users/ExampleUser/secret-footer.png",
                        "iconCanonicalUrl": "file:///C:/Users/ExampleUser/secret-footer-canonical.png"
                      },
                      "inlineEmojis": [
                        {
                          "id": "12345",
                          "name": "local",
                          "code": "local",
                          "isAnimated": false,
                          "imageUrl": "C:/Users/ExampleUser/secret-emoji.png"
                        }
                      ]
                    }
                  ],
                  "stickers": [
                    {
                      "id": "30",
                      "name": "sticker",
                      "format": "Png",
                      "sourceUrl": "file:///C:/Users/ExampleUser/secret-sticker.png"
                    }
                  ],
                  "reactions": [
                    {
                      "emoji": {
                        "id": "12345",
                        "name": "local",
                        "isAnimated": false,
                        "imageUrl": "file:///C:/Users/ExampleUser/secret-reaction.png"
                      },
                      "count": 1
                    }
                  ],
                  "mentions": [],
                  "inlineEmojis": [
                    {
                      "id": "12345",
                      "name": "local",
                      "code": "local",
                      "isAnimated": false,
                      "imageUrl": "file:///C:/Users/ExampleUser/secret-inline.png"
                    }
                  ]
                }
              ],
              "messageCount": 1,
              "conversionData": {
                "schemaVersion": 1,
                "members": [
                  {
                    "id": "10",
                    "displayName": "alice",
                    "avatarUrl": "file:///C:/Users/ExampleUser/secret-member.png",
                    "colorHex": null,
                    "roleIds": []
                  }
                ],
                "roles": [],
                "channels": [],
                "emojis": [
                  {
                    "id": "12345",
                    "name": "local",
                    "isAnimated": false,
                    "imageUrl": "file:///C:/Users/ExampleUser/secret-conversion-emoji.png"
                  }
                ]
              }
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "unsafe-asset-urls.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var html = await File.ReadAllTextAsync(htmlOut);
        html.Should().NotContain("file:", "local file URIs from converted JSON are unsafe");
        html.Should().NotContain("C:/Users/ExampleUser", "absolute Windows paths are unsafe");
        html.Should().NotContain("//server/share", "protocol-relative UNC-style URLs are unsafe");
        html.Should().NotContain("/etc/secret", "rooted Unix-style paths are unsafe");
        html.Should().Contain("https://example.com/page", "normal embed links are not asset URLs");
    }

    [Fact]
    public async Task Converter_uses_conversion_data_channel_kind_for_channel_mentions()
    {
        var jsonPath = await WriteRawJsonAsync(
            "voice-channel-mention.json",
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
                  "content": "Voice channel mention: <#30>",
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
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1,
              "conversionData": {
                "schemaVersion": 1,
                "members": [],
                "roles": [],
                "channels": [
                  {
                    "id": "30",
                    "name": "voice-room",
                    "type": "GuildVoiceChat",
                    "isVoice": true
                  }
                ]
              }
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "voice-channel-mention.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var text = Html.Parse(await File.ReadAllTextAsync(htmlOut)).Body?.TextContent;
        text.Should().Contain("Voice channel mention: 🔊voice-room");
        text.Should().NotContain("#voice-room");
    }

    [Fact]
    public async Task Converter_rejects_json_as_a_target_format()
    {
        var jsonPath = await WriteJsonAsync();
        var jsonOut = Path.Combine(_dir, "out.json");

        await FluentActions
            .Awaiting(() =>
                ExportConverter.ConvertAsync(jsonPath, jsonOut, ExportFormat.Json).AsTask()
            )
            .Should()
            .ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Converter_wraps_malformed_conversion_data_ids_as_invalid_export()
    {
        var jsonPath = await WriteRawJsonAsync(
            "malformed-conversion-data-id.json",
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
                  "content": "hello",
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
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1,
              "conversionData": {
                "schemaVersion": 1,
                "members": [
                  {
                    "id": "10",
                    "displayName": "alice",
                    "avatarUrl": null,
                    "colorHex": null,
                    "roleIds": ["not-a-snowflake"]
                  }
                ],
                "roles": [],
                "channels": [],
                "emojis": []
              }
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "malformed-conversion-data-id.html");

        var exception = await FluentActions
            .Awaiting(() =>
                ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark).AsTask()
            )
            .Should()
            .ThrowAsync<InvalidExportException>();

        exception.Which.InnerException.Should().BeOfType<FormatException>();
    }
}
