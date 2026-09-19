namespace DiscordChatExporter.Core.Exporting.Library;

// Full-text search result from a SQLite export, including the source database path.
public sealed record SqliteSearchHit(
    string DatabaseFilePath,
    string MessageId,
    string Timestamp,
    string AuthorName,
    string Snippet
);
