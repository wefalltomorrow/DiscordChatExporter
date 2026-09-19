using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Library;

public sealed class ContinueExportDiscoverySpecs : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DceContinueDiscovery_" + Guid.NewGuid().ToString("N")
    );

    public ContinueExportDiscoverySpecs() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private async Task<string> WriteEntryAsync(
        string subDir,
        Snowflake channelId,
        string fileName,
        string format,
        DateTimeOffset exportedAt,
        bool partitioned = false,
        bool createFile = true
    )
    {
        var dir = Path.Combine(_root, subDir);
        Directory.CreateDirectory(dir);

        if (createFile)
            File.WriteAllText(Path.Combine(dir, fileName), "");

        var entry = new ManifestEntry(
            "1",
            "Guild",
            channelId.ToString(),
            "channel-" + channelId,
            null,
            fileName,
            format,
            5,
            null,
            null,
            null,
            null,
            null,
            100,
            "sha",
            partitioned,
            exportedAt
        );

        await ManifestWriter.WriteAsync(dir, [entry], DateTimeOffset.UnixEpoch);
        return dir;
    }

    [Fact]
    public async Task Resolves_single_export_with_absolute_path_and_format()
    {
        var channelId = new Snowflake(100);
        var dir = await WriteEntryAsync(
            "one",
            channelId,
            "export.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch
        );

        var result = await ContinueExportDiscovery.ResolveAsync([dir], [channelId]);

        var resolved = result.Resolved.Should().ContainSingle().Subject;
        resolved.ChannelId.Should().Be(channelId);
        resolved.FilePath.Should().Be(Path.Combine(dir, "export.json"));
        resolved.Format.Should().Be(ExportFormat.Json);
        result.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolves_exact_format_from_catalog_not_extension()
    {
        var channelId = new Snowflake(100);
        var dir = await WriteEntryAsync(
            "one",
            channelId,
            "export.html",
            nameof(ExportFormat.HtmlLight),
            DateTimeOffset.UnixEpoch
        );

        var result = await ContinueExportDiscovery.ResolveAsync([dir], [channelId]);

        result.Resolved.Should().ContainSingle().Which.Format.Should().Be(ExportFormat.HtmlLight);
    }

    [Fact]
    public async Task Picks_most_recent_export_by_ExportedAt_across_dirs()
    {
        var channelId = new Snowflake(100);
        var older = await WriteEntryAsync(
            "older",
            channelId,
            "old.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch
        );
        var newer = await WriteEntryAsync(
            "newer",
            channelId,
            "new.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch.AddDays(1)
        );

        var result = await ContinueExportDiscovery.ResolveAsync([older, newer], [channelId]);

        result
            .Resolved.Should()
            .ContainSingle()
            .Which.FilePath.Should()
            .Be(Path.Combine(newer, "new.json"));
    }

    [Fact]
    public async Task Falls_back_to_older_usable_export_when_newest_is_partitioned()
    {
        var channelId = new Snowflake(100);
        var older = await WriteEntryAsync(
            "older",
            channelId,
            "old.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch
        );
        var newer = await WriteEntryAsync(
            "newer",
            channelId,
            "new.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch.AddDays(1),
            partitioned: true
        );

        var result = await ContinueExportDiscovery.ResolveAsync([older, newer], [channelId]);

        result
            .Resolved.Should()
            .ContainSingle()
            .Which.FilePath.Should()
            .Be(Path.Combine(older, "old.json"));
        result.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_partitioned_only_export_as_Unresolved_Partitioned()
    {
        var channelId = new Snowflake(100);
        var dir = await WriteEntryAsync(
            "one",
            channelId,
            "export.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch,
            partitioned: true
        );

        var result = await ContinueExportDiscovery.ResolveAsync([dir], [channelId]);

        result.Resolved.Should().BeEmpty();
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new UnresolvedCatalogChannel(channelId, ContinueSkipReason.Partitioned));
    }

    [Fact]
    public async Task Skips_channel_with_no_prior_export_as_Unresolved_NoPriorExport()
    {
        var exportedChannelId = new Snowflake(100);
        var selectedChannelId = new Snowflake(200);
        var dir = await WriteEntryAsync(
            "one",
            exportedChannelId,
            "export.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch
        );

        var result = await ContinueExportDiscovery.ResolveAsync([dir], [selectedChannelId]);

        result.Resolved.Should().BeEmpty();
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new UnresolvedCatalogChannel(selectedChannelId, ContinueSkipReason.NoPriorExport));
    }

    [Fact]
    public async Task Skips_entry_whose_file_is_missing_as_Unresolved_FileMissing()
    {
        var channelId = new Snowflake(100);
        var dir = await WriteEntryAsync(
            "one",
            channelId,
            "missing.json",
            nameof(ExportFormat.Json),
            DateTimeOffset.UnixEpoch,
            createFile: false
        );

        var result = await ContinueExportDiscovery.ResolveAsync([dir], [channelId]);

        result.Resolved.Should().BeEmpty();
        result
            .Unresolved.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new UnresolvedCatalogChannel(channelId, ContinueSkipReason.FileMissing));
    }

    [Fact]
    public async Task Skips_unsupported_or_unparseable_format_as_Unresolved()
    {
        var unsupportedChannelId = new Snowflake(100);
        var unknownChannelId = new Snowflake(200);
        var d1 = await WriteEntryAsync(
            "unsupported",
            unsupportedChannelId,
            "export.txt",
            nameof(ExportFormat.PlainText),
            DateTimeOffset.UnixEpoch
        );
        var d2 = await WriteEntryAsync(
            "unknown",
            unknownChannelId,
            "export.json",
            "Wat",
            DateTimeOffset.UnixEpoch
        );

        var result = await ContinueExportDiscovery.ResolveAsync(
            [d1, d2],
            [unsupportedChannelId, unknownChannelId]
        );

        result.Resolved.Should().BeEmpty();
        result
            .Unresolved.Should()
            .BeEquivalentTo([
                new UnresolvedCatalogChannel(
                    unsupportedChannelId,
                    ContinueSkipReason.UnsupportedFormat
                ),
                new UnresolvedCatalogChannel(unknownChannelId, ContinueSkipReason.UnknownFormat),
            ]);
    }

    [Fact]
    public async Task Partial_resolution_returns_resolved_and_unresolved_disjoint_sets()
    {
        var resolvedChannelId = new Snowflake(100);
        var unresolvedChannelId = new Snowflake(200);
        var dir = await WriteEntryAsync(
            "one",
            resolvedChannelId,
            "export.db",
            nameof(ExportFormat.Db),
            DateTimeOffset.UnixEpoch
        );

        var result = await ContinueExportDiscovery.ResolveAsync(
            [dir],
            [resolvedChannelId, unresolvedChannelId]
        );

        result.Resolved.Select(r => r.ChannelId).Should().Equal(resolvedChannelId);
        result.Unresolved.Select(u => u.ChannelId).Should().Equal(unresolvedChannelId);
        result
            .Resolved.Select(r => r.ChannelId)
            .Should()
            .NotIntersectWith(result.Unresolved.Select(u => u.ChannelId));
    }
}
