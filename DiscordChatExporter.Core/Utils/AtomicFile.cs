using System;
using System.IO;

namespace DiscordChatExporter.Core.Utils;

public static class AtomicFile
{
    public static string CreateSiblingTempPath(string destinationPath, string suffix)
    {
        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? ".";
        var fileName = Path.GetFileName(destinationPath);

        return Path.Combine(
            directoryPath,
            $"{fileName}.{Environment.ProcessId}.{Guid.NewGuid():N}{suffix}"
        );
    }

    // Atomically swaps a freshly-written temp file in for an existing destination. File.Replace
    // needs a backup path for its own crash-safety window (if the swap is interrupted the original
    // can be recovered from it), but once the swap succeeds that backup is just clutter sitting
    // next to the output — so delete it best-effort. Continue merges and manifest updates both use
    // this; without the cleanup, every continue left a ".bak" (sometimes hundreds of MB) behind.
    public static void ReplaceWithBackupCleanup(string sourcePath, string destinationPath)
    {
        var backupPath = CreateSiblingTempPath(destinationPath, ".bak");
        File.Replace(sourcePath, destinationPath, backupPath);

        try
        {
            File.Delete(backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a leftover .bak is harmless and must never fail the operation.
        }
    }
}
