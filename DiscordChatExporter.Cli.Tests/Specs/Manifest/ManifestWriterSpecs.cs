using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestWriterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"dce-manifest-{Guid.NewGuid():N}"
    );

    public ManifestWriterSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static ManifestEntry Entry(string file, long messageCount) =>
        new(
            GuildId: "1",
            GuildName: "g",
            ChannelId: "2",
            ChannelName: "c",
            CategoryName: null,
            File: file,
            Format: "Json",
            MessageCount: messageCount,
            FirstMessageId: "10",
            FirstMessageTimestamp: DateTimeOffset.UnixEpoch,
            LastMessageId: "20",
            LastMessageTimestamp: DateTimeOffset.UnixEpoch,
            AssetCount: 0,
            FileSizeBytes: 1,
            Sha256: "x",
            Partitioned: false,
            ExportedAt: DateTimeOffset.UnixEpoch
        );

    [Fact]
    public async Task Writing_creates_a_manifest_with_the_given_entries_and_schema_version()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().Be(ExportManifest.CurrentSchemaVersion);
        manifest.Entries.Should().ContainSingle(e => e.File == "a.json");
    }

    [Fact]
    public async Task Write_does_not_overwrite_a_present_but_unreadable_manifest()
    {
        var path = Path.Combine(_dir, ExportManifest.FileName);
        const string garbage = "{ this is not a valid manifest";
        await File.WriteAllTextAsync(path, garbage);

        var act = async () =>
            await ManifestWriter.WriteAsync(_dir, [Entry("new.json", 1)], DateTimeOffset.UnixEpoch);

        await act.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(path)).Should().Be(garbage);
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Write_does_not_overwrite_a_future_schema_manifest()
    {
        var path = Path.Combine(_dir, ExportManifest.FileName);
        const string futureManifest = """
            {
              "schemaVersion": 999,
              "generatedAt": "2026-01-01T00:00:00+00:00",
              "entries": []
            }
            """;
        await File.WriteAllTextAsync(path, futureManifest);

        var act = async () =>
            await ManifestWriter.WriteAsync(_dir, [Entry("new.json", 1)], DateTimeOffset.UnixEpoch);

        await act.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(path)).Should().Be(futureManifest);
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Writing_again_replaces_entries_for_the_same_file_and_keeps_the_others()
    {
        await ManifestWriter.WriteAsync(
            _dir,
            [Entry("a.json", 1), Entry("b.json", 1)],
            DateTimeOffset.UnixEpoch
        );
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 99)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest!.Entries.Should().HaveCount(2);
        manifest.Entries.Single(e => e.File == "a.json").MessageCount.Should().Be(99);
        manifest.Entries.Single(e => e.File == "b.json").MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Updating_exposes_the_existing_manifest_to_the_entry_factory()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);
        var sawExistingEntry = false;

        await ManifestWriter.UpdateAsync(
            _dir,
            existing =>
            {
                sawExistingEntry = existing?.Entries.Single().MessageCount == 1;
                return [Entry("a.json", 2)];
            },
            DateTimeOffset.UnixEpoch
        );

        sawExistingEntry.Should().BeTrue();
        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest!.Entries.Single(e => e.File == "a.json").MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Writing_over_an_existing_manifest_cleans_up_the_backup()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 2)], DateTimeOffset.UnixEpoch);

        // The overwrite is an atomic replace whose .bak is only a transient crash-safety net; it
        // must be cleaned up on success so backups don't pile up next to the export.
        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest!.Entries.Single(e => e.File == "a.json").MessageCount.Should().Be(2);
        File.Exists(Path.Combine(_dir, ExportManifest.FileName + ".bak")).Should().BeFalse();
    }

    [Fact]
    public async Task Write_does_not_touch_fixed_temp_or_backup_sentinel_files()
    {
        var path = Path.Combine(_dir, ExportManifest.FileName);
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);
        await File.WriteAllTextAsync(path + ".tmp", "user temp sentinel");
        await File.WriteAllTextAsync(path + ".bak", "user backup sentinel");

        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 2)], DateTimeOffset.UnixEpoch);

        (await File.ReadAllTextAsync(path + ".tmp")).Should().Be("user temp sentinel");
        (await File.ReadAllTextAsync(path + ".bak")).Should().Be("user backup sentinel");
    }

    [Fact]
    public async Task Concurrent_writes_to_the_same_manifest_do_not_lose_entries()
    {
        // Fire many parallel writes, each adding a distinct file. Without serialization,
        // the read-merge-write race would clobber entries (last-writer-wins on the whole file).
        const int count = 50;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            async (i, ct) =>
                await ManifestWriter.WriteAsync(
                    _dir,
                    [Entry($"file{i}.json", i)],
                    DateTimeOffset.UnixEpoch,
                    ct
                )
        );

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest.Should().NotBeNull();
        manifest!.Entries.Should().HaveCount(count);
    }
}
