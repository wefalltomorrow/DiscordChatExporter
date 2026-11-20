using System;
using System.IO;

namespace DiscordChatExporter.Core.Exporting;

internal static class ExportAssetPath
{
    // Only paths within the export directory travel with the archive.
    public static string GetPortablePath(string exportDirectory, string assetPath)
    {
        var fullPath = Path.GetFullPath(assetPath);
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(string.IsNullOrEmpty(exportDirectory) ? "." : exportDirectory),
            fullPath
        );
        return
            Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal
            )
            ? fullPath
            : relativePath;
    }
}
