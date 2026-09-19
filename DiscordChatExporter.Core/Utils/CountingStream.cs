using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Utils;

// Read-only pass-through stream that reports how many bytes were actually read out of it.
//
// Counting responses any other way isn't reliable: Discord's API answers with chunked transfer
// encoding and no Content-Length at all, so the only honest measure of what came down the wire is
// what the reader consumed.
internal class CountingStream(Stream inner, Action<long> onRead) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private int Count(int bytesRead)
    {
        if (bytesRead > 0)
            onRead(bytesRead);

        return bytesRead;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => Count(await inner.ReadAsync(buffer, cancellationToken));

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
