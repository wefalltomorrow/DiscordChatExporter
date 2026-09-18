using System;
using DiscordChatExporter.Core.Exceptions;

namespace DiscordChatExporter.Core.Exporting.Continuation;

// Non-fatal so the GUI can catch it via `when (!ex.IsFatal)`.
public class InvalidExportException(string message, Exception? innerException = null)
    : DiscordChatExporterException(message, false, innerException);
