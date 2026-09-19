using System.Threading;

namespace DiscordChatExporter.Core.Exporting;

// Live counters for one channel's export, so that the progress display can show what the export
// is actually doing rather than just how far along it thinks it is.
//
// The instance is ambient rather than passed around, because the two things worth counting sit at
// opposite ends of the call tree: bytes and requests are only visible inside the shared
// HttpClient's handler, which has no idea which channel it is working for, while messages and
// output bytes are only visible in the writer. An AsyncLocal bridges the two without threading a
// parameter through every signature in between.
//
// Consequence worth knowing when reading the numbers: work that several channels share is charged
// to whichever one triggered it. A member lookup is cached and awaited by everyone who needs it,
// but only the channel whose task actually issued the request pays for it here.
public class ExportStats
{
    private static readonly AsyncLocal<ExportStats?> CurrentLocal = new();

    public static ExportStats? Current
    {
        get => CurrentLocal.Value;
        set => CurrentLocal.Value = value;
    }

    private long _messagesExported;
    private long _bytesExported;
    private long _bytesDownloaded;
    private long _requestCount;

    public long MessagesExported => Interlocked.Read(ref _messagesExported);

    public long BytesExported => Interlocked.Read(ref _bytesExported);

    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    public long RequestCount => Interlocked.Read(ref _requestCount);

    // Only Discord's own count for a thread; ordinary channels don't carry one at all. Left null
    // whenever it wouldn't be comparable to what the export will actually write.
    public int? TotalMessages { get; set; }

    // The writer tracks both of these itself, and it tracks them as running totals rather than as
    // increments, so they're assigned rather than added to
    public void ReportExported(long messages, long bytes)
    {
        Interlocked.Exchange(ref _messagesExported, messages);
        Interlocked.Exchange(ref _bytesExported, bytes);
    }

    public void ReportRequest() => Interlocked.Increment(ref _requestCount);

    public void ReportDownloadedBytes(long bytes) => Interlocked.Add(ref _bytesDownloaded, bytes);
}
