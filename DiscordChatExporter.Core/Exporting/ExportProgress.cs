using System;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

// Progress snapshot for a channel export, including fraction, messages read, and current timestamp.
public readonly record struct ExportProgress(
    Percentage Fraction,
    long MessagesRead,
    DateTimeOffset? CurrentTimestamp
);
