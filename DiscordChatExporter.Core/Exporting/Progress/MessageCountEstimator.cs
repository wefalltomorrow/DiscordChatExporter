using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting.Progress;

public readonly record struct MessageDensitySample(
    DateTimeOffset Timestamp,
    double MessagesPerSecond
);

public static class MessageCountEstimator
{
    public const int PageSize = 100;

    public static bool ShouldEstimate(int firstPageCount, bool hasMoreMessages) =>
        firstPageCount >= PageSize && hasMoreMessages;

    public static long? EstimateTotal(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<MessageDensitySample> samples
    )
    {
        if (end <= start)
            return 0;

        var ordered = samples
            .Where(s => s.MessagesPerSecond > 0)
            .OrderBy(s => s.Timestamp)
            .ToArray();

        if (ordered.Length == 0)
            return null;

        var points = new List<MessageDensitySample>(ordered.Length + 2);
        if (ordered[0].Timestamp > start)
            points.Add(new MessageDensitySample(start, ordered[0].MessagesPerSecond));

        points.AddRange(ordered.Where(s => s.Timestamp >= start && s.Timestamp <= end));

        if (points.Count == 0)
        {
            var nearest = ordered[^1].Timestamp < start ? ordered[^1] : ordered[0];
            points.Add(new MessageDensitySample(start, nearest.MessagesPerSecond));
        }

        if (points[^1].Timestamp < end)
            points.Add(new MessageDensitySample(end, points[^1].MessagesPerSecond));

        if (points[0].Timestamp > start)
            points.Insert(0, new MessageDensitySample(start, points[0].MessagesPerSecond));

        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var seconds = (current.Timestamp - previous.Timestamp).TotalSeconds;
            if (seconds <= 0)
                continue;

            total += seconds * (previous.MessagesPerSecond + current.MessagesPerSecond) / 2;
        }

        return Math.Max(0, (long)Math.Round(total));
    }
}
