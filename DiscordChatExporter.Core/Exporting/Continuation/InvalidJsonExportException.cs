using System;

namespace DiscordChatExporter.Core.Exporting.Continuation;

// Non-fatal so the GUI can catch it via `when (!ex.IsFatal)`.
public class InvalidJsonExportException(string message, Exception? innerException = null)
    : InvalidExportException(message, innerException);
