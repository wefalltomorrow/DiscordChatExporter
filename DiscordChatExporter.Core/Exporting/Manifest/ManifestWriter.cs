using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestWriter
{
    // Serializes the read-merge-write of a given manifest file so per-channel checkpoint writes
    // from the parallel export loop don't clobber each other. Keyed by manifest path; MUST be static.
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // Writes/updates <dirPath>/manifest.json. Entries from the same output family are replaced
    // as a set so re-exporting a channel with fewer partitions cannot leave stale [part N] records.
    // Atomic: writes a temp file then swaps it in, keeping a .bak of the previous manifest.
    public static async ValueTask WriteAsync(
        string dirPath,
        IReadOnlyList<ManifestEntry> newEntries,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    ) =>
        await UpdateCoreAsync(
            dirPath,
            _ => newEntries,
            existingEntry =>
                newEntries.Any(newEntry =>
                    HasSameExportIdentity(existingEntry, newEntry)
                    && ManifestFileFamily.IsSameFamily(existingEntry.File, newEntry.File)
                ),
            now,
            cancellationToken
        );

    public static async ValueTask UpdateAsync(
        string dirPath,
        Func<ExportManifest?, IReadOnlyList<ManifestEntry>> createEntries,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    ) => await UpdateCoreAsync(dirPath, createEntries, null, now, cancellationToken);

    private static bool HasSameExportIdentity(ManifestEntry left, ManifestEntry right) =>
        left.GuildId == right.GuildId
        && left.ChannelId == right.ChannelId
        && left.Format == right.Format;

    private static async ValueTask UpdateCoreAsync(
        string dirPath,
        Func<ExportManifest?, IReadOnlyList<ManifestEntry>> createEntries,
        Func<ManifestEntry, bool>? shouldRemoveExistingEntry,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var manifestPath = Path.Combine(dirPath, ExportManifest.FileName);

        using var _ = await Locker.LockAsync(manifestPath, cancellationToken);

        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var existing = await ManifestReader.TryReadAsync(manifestPath, cancellationToken);
        // "null" means either absent (safe to start fresh) or present-but-unreadable
        // (locked/corrupt). Overwriting the latter would silently discard every other channel's
        // entry, so refuse and leave the bytes untouched.
        if (existing is null && File.Exists(manifestPath))
        {
            throw new IOException(
                $"The manifest '{manifestPath}' exists but could not be read, so it will not be overwritten "
                    + "(that would lose other channels' entries). Close any app locking it, or remove "
                    + "the corrupted manifest, then retry."
            );
        }

        if (existing is not null)
        {
            foreach (var entry in existing.Entries)
            {
                if (shouldRemoveExistingEntry?.Invoke(entry) != true)
                    byFile[entry.File] = entry;
            }
        }

        var newEntries = createEntries(existing);
        foreach (var entry in newEntries)
            byFile[entry.File] = entry;

        var merged = new ExportManifest(
            ExportManifest.CurrentSchemaVersion,
            now,
            byFile.Values.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToArray()
        );

        Directory.CreateDirectory(dirPath);

        var tempPath = AtomicFile.CreateSiblingTempPath(manifestPath, ".tmp");

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    merged,
                    ManifestJsonContext.Default.ExportManifest,
                    cancellationToken
                );
            }

            if (File.Exists(manifestPath))
                AtomicFile.ReplaceWithBackupCleanup(tempPath, manifestPath);
            else
                File.Move(tempPath, manifestPath);
        }
        catch
        {
            // A failed write must not leave a stale temp file in the user's output folder.
            try
            {
                File.Delete(tempPath);
            }
            catch
            { /* best-effort temp cleanup */
            }
            throw;
        }
    }
}
