using System;
using System.IO;
using System.Linq;
using System.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestBuilderSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"dce-manifest-{Guid.NewGuid():N}"
    );

    public ManifestBuilderSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ManifestChannelInfo Info() => new("1", "Guild", "2", "general", "Text", "Json");

    [Fact]
    public void A_single_file_export_produces_one_self_describing_entry()
    {
        var path = WriteFile("general.json", "hello");
        var result = new ExportResult(
            [
                new ExportedFile(
                    path,
                    3,
                    new Snowflake(100),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(300),
                    DateTimeOffset.UnixEpoch.AddHours(1)
                ),
            ],
            3,
            7
        );

        var entries = ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.GuildId.Should().Be("1");
        entry.ChannelId.Should().Be("2");
        entry.CategoryName.Should().Be("Text");
        entry.File.Should().Be("general.json");
        entry.MessageCount.Should().Be(3);
        entry.FirstMessageId.Should().Be("100");
        entry.LastMessageId.Should().Be("300");
        entry.AssetCount.Should().Be(7);
        entry.FileSizeBytes.Should().Be(5); // "hello"
        entry.Sha256.Should().NotBeNullOrEmpty();
        entry.Partitioned.Should().BeFalse();
    }

    [Fact]
    public void A_partitioned_export_produces_one_entry_per_file_all_flagged_partitioned()
    {
        var p1 = WriteFile("a.json", "x");
        var p2 = WriteFile("b.json", "yy");
        var result = new ExportResult(
            [
                new ExportedFile(
                    p1,
                    1,
                    new Snowflake(1),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(2),
                    DateTimeOffset.UnixEpoch
                ),
                new ExportedFile(
                    p2,
                    1,
                    new Snowflake(3),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(4),
                    DateTimeOffset.UnixEpoch
                ),
            ],
            2,
            0
        );

        var entries = ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch);

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Partitioned);
    }

    [Fact]
    public void An_unreadable_output_file_is_skipped_and_the_rest_are_still_catalogued()
    {
        var existing = WriteFile("present.json", "hi");
        var missing = Path.Combine(_dir, "does-not-exist.json");
        var result = new ExportResult(
            [
                new ExportedFile(
                    existing,
                    1,
                    new Snowflake(1),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(2),
                    DateTimeOffset.UnixEpoch
                ),
                new ExportedFile(
                    missing,
                    1,
                    new Snowflake(3),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(4),
                    DateTimeOffset.UnixEpoch
                ),
            ],
            2,
            0
        );

        var entries = ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch);

        entries.Should().ContainSingle();
        entries[0].File.Should().Be("present.json");
    }

    [Fact]
    public void Build_honors_cancellation_before_hashing_output_files()
    {
        var path = WriteFile("general.json", "hello");
        var result = new ExportResult(
            [
                new ExportedFile(
                    path,
                    1,
                    new Snowflake(1),
                    DateTimeOffset.UnixEpoch,
                    new Snowflake(2),
                    DateTimeOffset.UnixEpoch
                ),
            ],
            1,
            0
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () =>
            ManifestBuilder.Build(Info(), result, DateTimeOffset.UnixEpoch, cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
