using System;
using System.Linq;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Markdown.Parsing;

namespace DiscordChatExporter.Core.Exporting.Filtering;

internal class HasMessageFilter(MessageContentMatchKind kind) : MessageFilter
{
    public override bool IsMatch(Message message) =>
        kind switch
        {
            MessageContentMatchKind.Link => MarkdownParser.ExtractLinkUrls(message.Content).Any(),
            MessageContentMatchKind.Embed => message.Embeds.Any(),
            MessageContentMatchKind.File => message.Attachments.Any(),
            MessageContentMatchKind.Video => message.Attachments.Any(file => file.IsVideo),
            MessageContentMatchKind.Image => message.Attachments.Any(file => file.IsImage),
            MessageContentMatchKind.Sound => message.Attachments.Any(file => file.IsAudio),
            MessageContentMatchKind.Pin => message.IsPinned,
            MessageContentMatchKind.Invite => MarkdownParser
                .ExtractLinkUrls(message.Content)
                .Any(url => !string.IsNullOrWhiteSpace(Invite.TryGetCodeFromUrl(url))),
            _ => throw new InvalidOperationException(
                $"Unknown message content match kind '{kind}'."
            ),
        };
}
