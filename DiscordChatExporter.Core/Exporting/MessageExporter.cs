using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Core.Exporting;

internal partial class MessageExporter(ExportContext context) : IAsyncDisposable
{
    private int _partitionIndex;
    private MessageWriter? _writer;
    private bool _hasWriterInitializationFailed;

    private readonly List<MutableFileStats> _files = [];
    private MutableFileStats? _currentFile;

    public long MessagesExported { get; private set; }

    // Per-file stats captured during the export, in creation order.
    public IReadOnlyList<ExportedFile> Files => _files.Select(f => f.ToExportedFile()).ToArray();

    private async ValueTask<MessageWriter> InitializeWriterAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (
            context.Request.Format is ExportFormat.Db
            && context.Request.PartitionLimit is FileSizePartitionLimit
        )
        {
            _hasWriterInitializationFailed = true;
            throw new NotSupportedException(
                "SQLite exports do not support file-size partitioning. Use message-count partitioning or disable partitioning."
            );
        }

        // Ensure that the partition limit has not been reached
        if (
            _writer is not null
            && context.Request.PartitionLimit.IsReached(
                _writer.MessagesWritten,
                _writer.BytesWritten
            )
        )
        {
            await UninitializeWriterAsync(cancellationToken);
            _partitionIndex++;
        }

        // Writer is still valid, return
        if (_writer is not null)
            return _writer;

        Directory.CreateDirectory(context.Request.OutputDirPath);
        var filePath = GetPartitionFilePath(context.Request.OutputFilePath, _partitionIndex);

        var writer = CreateMessageWriter(filePath, context.Request.Format, context);
        try
        {
            await writer.WritePreambleAsync(cancellationToken);
        }
        catch
        {
            _hasWriterInitializationFailed = true;
            await writer.DisposeAsync();
            throw;
        }

        _currentFile = new MutableFileStats(filePath);
        _files.Add(_currentFile);

        return _writer = writer;
    }

    private async ValueTask UninitializeWriterAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            try
            {
                await _writer.WritePostambleAsync(cancellationToken);
            }
            // Writer must be disposed, even if it fails to write the postamble
            finally
            {
                await _writer.DisposeAsync();
                _writer = null;
            }
        }
    }

    private async ValueTask AbortWriterAsync()
    {
        if (_writer is null)
            return;

        await _writer.DisposeAsync();
        _writer = null;
    }

    public async ValueTask ExportMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        var writer = await InitializeWriterAsync(cancellationToken);
        await writer.WriteMessageAsync(message, cancellationToken);
        _currentFile!.Record(message);
        MessagesExported++;
    }

    internal async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await AbortWriterAsync();
            return;
        }

        // If no messages were written, force the creation of an empty file.
        if (MessagesExported <= 0 && !_hasWriterInitializationFailed)
            _ = await InitializeWriterAsync(cancellationToken);

        await UninitializeWriterAsync(cancellationToken);
    }

    internal async ValueTask AbortAsync() => await AbortWriterAsync();

    public async ValueTask DisposeAsync() => await DisposeAsync(CancellationToken.None);

    private sealed class MutableFileStats(string filePath)
    {
        private long _count;
        private Snowflake? _firstId;
        private DateTimeOffset? _firstTs;
        private Snowflake? _lastId;
        private DateTimeOffset? _lastTs;

        public void Record(Message message)
        {
            if (IsBefore(message, _firstTs, _firstId))
            {
                _firstId = message.Id;
                _firstTs = message.Timestamp;
            }

            if (IsAfter(message, _lastTs, _lastId))
            {
                _lastId = message.Id;
                _lastTs = message.Timestamp;
            }

            _count++;
        }

        public ExportedFile ToExportedFile() =>
            new(filePath, _count, _firstId, _firstTs, _lastId, _lastTs);

        private static bool IsBefore(Message message, DateTimeOffset? timestamp, Snowflake? id) =>
            timestamp is null
            || message.Timestamp < timestamp
            || (message.Timestamp == timestamp && (id is null || message.Id < id.Value));

        private static bool IsAfter(Message message, DateTimeOffset? timestamp, Snowflake? id) =>
            timestamp is null
            || message.Timestamp > timestamp
            || (message.Timestamp == timestamp && (id is null || message.Id > id.Value));
    }
}

internal partial class MessageExporter
{
    private static string GetPartitionFilePath(string baseFilePath, int partitionIndex)
    {
        // First partition, don't change the file name
        if (partitionIndex <= 0)
            return baseFilePath;

        // Inject partition index into the file name
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(baseFilePath);
        var fileExt = Path.GetExtension(baseFilePath);
        var fileName = $"{fileNameWithoutExt} [part {partitionIndex + 1}]{fileExt}";
        var dirPath = Path.GetDirectoryName(baseFilePath);

        return !string.IsNullOrWhiteSpace(dirPath) ? Path.Combine(dirPath, fileName) : fileName;
    }

    private static MessageWriter CreateMessageWriter(
        string filePath,
        ExportFormat format,
        ExportContext context
    ) =>
        format switch
        {
            ExportFormat.PlainText => new PlainTextMessageWriter(File.Create(filePath), context),
            ExportFormat.Csv => new CsvMessageWriter(File.Create(filePath), context),
            ExportFormat.HtmlDark => new HtmlMessageWriter(File.Create(filePath), context, "Dark"),
            ExportFormat.HtmlLight => new HtmlMessageWriter(
                File.Create(filePath),
                context,
                "Light"
            ),
            ExportFormat.Json => new JsonMessageWriter(File.Create(filePath), context),
            ExportFormat.Db => new SqliteMessageWriter(filePath, context),
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                $"Unknown export format '{format}'."
            ),
        };
}
