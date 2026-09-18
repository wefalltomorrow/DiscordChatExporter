using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

// Per-channel contribution to the run summary.
public sealed record ChannelExportStats(long MessageCount, long AssetCount, long ByteSize);

// Aggregate outcome of an export run, for the completion summary.
public sealed record ExportSummary(
    int SucceededChannels,
    int FailedChannels,
    long TotalMessages,
    long TotalAssets,
    long TotalBytes,
    TimeSpan Duration
);

public static class ExportSummarizer
{
    public static ExportSummary Summarize(
        IReadOnlyList<ChannelExportStats> succeeded,
        int failedChannels,
        TimeSpan duration
    ) =>
        new(
            SucceededChannels: succeeded.Count,
            FailedChannels: failedChannels,
            TotalMessages: succeeded.Sum(s => s.MessageCount),
            TotalAssets: succeeded.Sum(s => s.AssetCount),
            TotalBytes: succeeded.Sum(s => s.ByteSize),
            Duration: duration
        );
}
