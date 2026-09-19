using System;
using System.Text.RegularExpressions;

namespace DiscordChatExporter.Core.Markdown.Parsing;

internal class RegexMatcher<TContext, TValue>(
    Regex regex,
    Func<TContext, StringSegment, Match, TValue?> transform,
    bool isLineAnchored = false
) : IMatcher<TContext, TValue>
{
    public ParsedMatch<TValue>? TryMatch(TContext context, StringSegment segment)
    {
        var match = regex.Match(segment.Source, segment.StartIndex, segment.Length);
        if (!match.Success)
            return null;

        if (isLineAnchored && match.Index > 0 && segment.Source[match.Index - 1] is not '\n')
            return null;

        var segmentMatch = segment.Relocate(match);
        var value = transform(context, segmentMatch, match);

        return value is not null ? new ParsedMatch<TValue>(segmentMatch, value) : null;
    }
}
