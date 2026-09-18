using System;
using System.IO;
using System.Linq;
using System.Threading;
using DiscordChatExporter.Core.Exporting;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestResume
{
    public static bool IsAlreadyExported(
        ExportManifest? manifest,
        string dirPath,
        ExportRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (manifest is null)
            return false;

        var fileName = Path.GetFileName(request.OutputFilePath);
        var entry = manifest.Entries.FirstOrDefault(e =>
            string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase)
            && e.GuildId == request.Guild.Id.ToString()
            && e.ChannelId == request.Channel.Id.ToString()
            && e.Format == request.Format.ToString()
        );

        if (entry is null)
            return false;

        var filePath = Path.Combine(dirPath, entry.File);
        if (!File.Exists(filePath))
            return false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            return new FileInfo(filePath).Length == entry.FileSizeBytes
                && string.Equals(
                    ManifestBuilder.ComputeSha256(filePath, cancellationToken),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
