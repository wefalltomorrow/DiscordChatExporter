using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Markdown.Parsing;

namespace DiscordChatExporter.Core.Exporting;

internal abstract class MessageWriter(Stream stream, ExportContext context) : IAsyncDisposable
{
    protected Stream Stream { get; } = stream;

    protected ExportContext Context { get; } = context;

    public long MessagesWritten { get; private set; }

    public long BytesWritten => Stream.Length;

    // Formats markdown to plain text when the export requests it; otherwise passes it through.
    // Shared by the plain-text-based writers (PlainText, CSV, JSON, SQLite).
    protected async ValueTask<string> FormatMarkdownAsync(
        string markdown,
        CancellationToken cancellationToken = default
    ) =>
        Context.Request.ShouldFormatMarkdown
            ? await PlainTextMarkdownVisitor.FormatAsync(Context, markdown, cancellationToken)
            : markdown;

    public virtual ValueTask WritePreambleAsync(CancellationToken cancellationToken = default) =>
        default;

    public virtual ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        MessagesWritten++;
        return default;
    }

    public virtual ValueTask WritePostambleAsync(CancellationToken cancellationToken = default) =>
        default;

    public virtual async ValueTask DisposeAsync() => await Stream.DisposeAsync();
}
