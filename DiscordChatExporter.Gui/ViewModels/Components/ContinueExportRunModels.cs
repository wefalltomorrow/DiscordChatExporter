using System.Collections.Generic;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Gui.ViewModels.Components;

internal sealed record ContinueExportFileResult(
    bool WasProcessed,
    long NewMessages,
    bool IsCatalogRefreshed
)
{
    public static ContinueExportFileResult Skipped { get; } = new(false, 0, true);
}

internal sealed record ContinueExportRunSummary(
    int ProcessedCount,
    long TotalNewMessages,
    bool CatalogWriteFailed,
    IReadOnlyList<Channel> FailedChannels
);

internal sealed record ResolvedContinueTarget(
    Channel Channel,
    Guild Guild,
    string FilePath,
    string Dir,
    ExportFormat Format,
    ContinuationCutoff Cutoff
);
