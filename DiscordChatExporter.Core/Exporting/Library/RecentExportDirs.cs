using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting.Library;

// Pure helper for the persisted most-recently-used export-folder list. Adds a directory to the
// front, removes any case-insensitive duplicate, and caps the length.
public static class RecentExportDirs
{
    public static IReadOnlyList<string> Add(IReadOnlyList<string> existing, string newDir, int max)
    {
        var result = new List<string> { newDir };
        result.AddRange(
            existing.Where(d => !string.Equals(d, newDir, StringComparison.OrdinalIgnoreCase))
        );
        return result.Take(max).ToArray();
    }
}
