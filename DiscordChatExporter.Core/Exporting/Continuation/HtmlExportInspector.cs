using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class HtmlExportInspector
{
    private const string MessageContainerClass = "chatlog__message-container";
    private const string MessageGroupClass = "chatlog__message-group";
    private const string ChatlogClass = "chatlog";
    private const string MessageIdAttribute = "data-message-id";

    internal readonly record struct MessageContainerTag(int Index, string MessageId);

    private readonly record struct DivOpenTag(
        int Index,
        int EndIndex,
        string ClassValue,
        string MessageId
    );

    private readonly record struct DivToken(
        bool IsOpen,
        int Index,
        int EndIndex,
        DivOpenTag OpenTag
    );

    // Returns data-message-id values from real message-container tags only. This skips forged ids in
    // message-body text and in user-controlled attributes rendered inside message bodies, such as
    // links with data-message-id in their href or attribute values containing class/data-message-id.
    internal static IReadOnlyList<string> ExtractMessageIdStrings(string html) =>
        FindMessageContainerTags(html).Select(tag => tag.MessageId).ToArray();

    internal static IReadOnlyList<MessageContainerTag> FindMessageContainerTags(string html)
    {
        var chatlog = FindFirstDivWithClass(html, ChatlogClass);
        if (chatlog is not null)
            return FindGroupedMessageContainerTags(html, chatlog.Value.EndIndex + 1, true);

        var groupedTags = FindGroupedMessageContainerTags(html, 0, false);
        return groupedTags.Count > 0 ? groupedTags : FindTopLevelMessageContainerTags(html);
    }

    internal static IReadOnlyList<int> FindTopLevelMessageGroupStarts(string html) =>
        FindTopLevelDivStartsByClass(html, MessageGroupClass);

    private static bool IsTagNameBoundary(char c) => char.IsWhiteSpace(c) || c is '>' or '/';

    private static DivOpenTag? FindFirstDivWithClass(string html, string className)
    {
        for (
            var index = 0;
            TryFindNextDivOpenTag(html, index, out var tag);
            index = tag.EndIndex + 1
        )
        {
            if (HasClassToken(tag.ClassValue, className))
                return tag;
        }

        return null;
    }

    private static IReadOnlyList<MessageContainerTag> FindGroupedMessageContainerTags(
        string html,
        int startIndex,
        bool stopAtDepthZeroClose
    )
    {
        var tags = new List<MessageContainerTag>();
        var depth = 0;
        var groupDepth = -1;

        for (
            var index = startIndex;
            TryFindNextDivToken(html, index, out var token);
            index = token.EndIndex + 1
        )
        {
            if (!token.IsOpen)
            {
                if (depth == 0)
                {
                    if (stopAtDepthZeroClose)
                        break;
                    continue;
                }

                if (depth == groupDepth)
                    groupDepth = -1;
                depth--;
                continue;
            }

            var tag = token.OpenTag;
            if (groupDepth < 0 && depth == 0 && HasClassToken(tag.ClassValue, MessageGroupClass))
                groupDepth = 1;
            else if (groupDepth >= 0 && depth == groupDepth && IsMessageContainer(tag))
                tags.Add(new MessageContainerTag(tag.Index, tag.MessageId));

            depth++;
        }

        return tags;
    }

    private static IReadOnlyList<MessageContainerTag> FindTopLevelMessageContainerTags(string html)
    {
        var tags = new List<MessageContainerTag>();
        var depth = 0;
        for (
            var index = 0;
            TryFindNextDivToken(html, index, out var token);
            index = token.EndIndex + 1
        )
        {
            if (!token.IsOpen)
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (depth == 0 && IsMessageContainer(token.OpenTag))
                tags.Add(new MessageContainerTag(token.OpenTag.Index, token.OpenTag.MessageId));

            depth++;
        }

        return tags;
    }

    private static IReadOnlyList<int> FindTopLevelDivStartsByClass(string html, string className)
    {
        var starts = new List<int>();
        var depth = 0;
        for (
            var index = 0;
            TryFindNextDivToken(html, index, out var token);
            index = token.EndIndex + 1
        )
        {
            if (!token.IsOpen)
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (depth == 0 && HasClassToken(token.OpenTag.ClassValue, className))
                starts.Add(token.OpenTag.Index);

            depth++;
        }

        return starts;
    }

    private static bool TryFindNextDivToken(string html, int startIndex, out DivToken token)
    {
        var hasOpen = TryFindNextDivOpenTag(html, startIndex, out var openTag);
        var closeIndex = html.IndexOf("</div>", startIndex, StringComparison.OrdinalIgnoreCase);
        if (closeIndex >= 0 && (!hasOpen || closeIndex < openTag.Index))
        {
            token = new DivToken(false, closeIndex, closeIndex + "</div>".Length - 1, default);
            return true;
        }

        if (hasOpen)
        {
            token = new DivToken(true, openTag.Index, openTag.EndIndex, openTag);
            return true;
        }

        token = default;
        return false;
    }

    private static bool TryFindNextDivOpenTag(string html, int startIndex, out DivOpenTag tag)
    {
        for (
            var tagStart = html.IndexOf("<div", startIndex, StringComparison.OrdinalIgnoreCase);
            tagStart >= 0;
            tagStart = html.IndexOf("<div", tagStart + 4, StringComparison.OrdinalIgnoreCase)
        )
        {
            var tagNameEnd = tagStart + 4;
            if (tagNameEnd < html.Length && !IsTagNameBoundary(html[tagNameEnd]))
                continue;

            var tagEnd = FindTagEnd(html, tagNameEnd);
            if (tagEnd < 0)
                break;

            tag = ReadDivOpenTag(html, tagStart, tagNameEnd, tagEnd);
            return true;
        }

        tag = default;
        return false;
    }

    private static int FindTagEnd(string html, int startIndex)
    {
        var quote = '\0';
        for (var i = startIndex; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '\"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
                return i;
        }

        return -1;
    }

    private static DivOpenTag ReadDivOpenTag(string html, int tagStart, int startIndex, int tagEnd)
    {
        var classValue = string.Empty;
        var messageId = string.Empty;

        for (var i = startIndex; i < tagEnd; )
        {
            while (i < tagEnd && char.IsWhiteSpace(html[i]))
                i++;

            if (i >= tagEnd || html[i] == '/')
                break;

            var nameStart = i;
            while (i < tagEnd && IsAttributeNameChar(html[i]))
                i++;

            if (nameStart == i)
            {
                i++;
                continue;
            }

            var name = html[nameStart..i];
            while (i < tagEnd && char.IsWhiteSpace(html[i]))
                i++;

            var value = string.Empty;
            if (i < tagEnd && html[i] == '=')
            {
                i++;
                while (i < tagEnd && char.IsWhiteSpace(html[i]))
                    i++;

                var valueStart = i;
                if (i < tagEnd && html[i] is '\"' or '\'')
                {
                    var quote = html[i++];
                    valueStart = i;
                    while (i < tagEnd && html[i] != quote)
                        i++;
                    value = html[valueStart..i];
                    if (i < tagEnd)
                        i++;
                }
                else
                {
                    while (i < tagEnd && !char.IsWhiteSpace(html[i]))
                        i++;
                    value = html[valueStart..i];
                }
            }

            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
                classValue = value;
            else if (name.Equals(MessageIdAttribute, StringComparison.OrdinalIgnoreCase))
                messageId = value;
        }

        return new DivOpenTag(tagStart, tagEnd, classValue, messageId);
    }

    private static bool IsMessageContainer(DivOpenTag tag) =>
        tag.MessageId.Length > 0 && HasClassToken(tag.ClassValue, MessageContainerClass);

    private static bool IsAttributeNameChar(char c) =>
        !char.IsWhiteSpace(c) && c is not '=' and not '>' and not '/';

    private static bool HasClassToken(string classValue, string expectedToken)
    {
        for (var i = 0; i < classValue.Length; )
        {
            while (i < classValue.Length && char.IsWhiteSpace(classValue[i]))
                i++;

            var tokenStart = i;
            while (i < classValue.Length && !char.IsWhiteSpace(classValue[i]))
                i++;

            if (
                i > tokenStart
                && classValue
                    .AsSpan(tokenStart, i - tokenStart)
                    .SequenceEqual(expectedToken.AsSpan())
            )
            {
                return true;
            }
        }

        return false;
    }

    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        var channelId =
            FileNameChannelId.TryParse(filePath)
            ?? throw new InvalidExportException(
                "Could not determine the channel for this HTML export. "
                    + "Keep the default file name (it includes the channel id) or re-export."
            );

        if (ContinuationFileName.HasBeforeBoundHint(filePath))
        {
            throw new InvalidExportException(
                "HTML exports with a 'before' date range cannot be continued safely. "
                    + "Continue the original JSON or SQLite export instead."
            );
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        var ids = ExtractMessageIdStrings(text)
            .Select(TryParseMessageId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToArray();

        if (ids.Length == 0)
            throw new InvalidExportException(
                "The HTML export contains no messages to continue from."
            );

        EnsureConsistentMessageOrder(ids);

        var first = ids[0];
        var last = ids[^1];
        return new ContinuationCutoff(
            channelId,
            last,
            null,
            IsChronological: first.Value <= last.Value,
            ExistingCount: ids.Length,
            CutoffIsExact: true
        );
    }

    private static Snowflake? TryParseMessageId(string value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? new Snowflake(id)
            : null;

    private static void EnsureConsistentMessageOrder(IReadOnlyList<Snowflake> ids)
    {
        var direction = 0;
        for (var i = 1; i < ids.Count; i++)
        {
            var comparison = ids[i].Value.CompareTo(ids[i - 1].Value);
            if (comparison == 0)
                throw new InvalidExportException("The HTML export contains duplicate messages.");

            var currentDirection = Math.Sign(comparison);
            if (direction == 0)
            {
                direction = currentDirection;
                continue;
            }

            if (direction != currentDirection)
            {
                throw new InvalidExportException(
                    "The HTML export's messages are not consistently ordered."
                );
            }
        }
    }
}
