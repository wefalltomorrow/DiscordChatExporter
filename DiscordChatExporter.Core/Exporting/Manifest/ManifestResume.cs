using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

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

        var baseFileName = Path.GetFileName(request.OutputFilePath);
        var baseEntry = manifest.Entries.FirstOrDefault(e =>
            string.Equals(e.File, baseFileName, StringComparison.OrdinalIgnoreCase)
            && IsSameExportIdentity(e, request)
        );

        if (baseEntry is null)
            return false;

        // A non-partitioned export is complete if its one catalogued file is intact.
        if (!baseEntry.Partitioned)
            return IsEntryIntact(baseEntry, dirPath, cancellationToken);

        // For partitioned exports, validating only the first file can incorrectly mark a
        // partially deleted/corrupted export as complete. Verify the whole partition family.
        var family = manifest
            .Entries.Where(e =>
                e.Partitioned
                && IsSameExportIdentity(e, request)
                && ManifestFileFamily.IsSameFamily(e.File, baseFileName)
            )
            .ToArray();

        if (family.Length < 2)
            return false;

        // A valid partition family is the base file followed by contiguous [part 2], [part 3], ...
        // entries. This deliberately fails closed if stale/missing entries make the family ambiguous.
        var partNumbers = new List<int>(family.Length);
        foreach (var entry in family)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(entry.File, baseFileName, StringComparison.OrdinalIgnoreCase))
            {
                partNumbers.Add(1);
                continue;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(entry.File);
            var match = ManifestFileFamily.PartitionSuffixRegex().Match(nameWithoutExtension);
            if (
                !match.Success
                || !int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var partNumber
                )
                || partNumber < 2
            )
            {
                return false;
            }

            partNumbers.Add(partNumber);
        }

        partNumbers.Sort();
        for (var i = 0; i < partNumbers.Count; i++)
        {
            if (partNumbers[i] != i + 1)
                return false;
        }

        return family.All(entry => IsEntryIntact(entry, dirPath, cancellationToken));
    }

    private static bool IsSameExportIdentity(ManifestEntry entry, ExportRequest request) =>
        entry.GuildId == request.Guild.Id.ToString()
        && entry.ChannelId == request.Channel.Id.ToString()
        && entry.Format == request.Format.ToString();

    private static bool IsEntryIntact(
        ManifestEntry entry,
        string dirPath,
        CancellationToken cancellationToken
    )
    {
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
