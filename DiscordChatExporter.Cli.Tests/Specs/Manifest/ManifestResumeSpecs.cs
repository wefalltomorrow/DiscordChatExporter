using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestResumeSpecs
{
    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ManifestEntry Entry(string file, long fileSizeBytes = 0, string sha256 = "x") =>
        new(
            "1",
            "g",
            "2",
            "c",
            null,
            file,
            "Json",
            0,
            null,
            null,
            null,
            null,
            null,
            fileSizeBytes,
            sha256,
            false,
            DateTimeOffset.UnixEpoch
        );

    private static ExportManifest Manifest(params string[] files) =>
        new(
            ExportManifest.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            files.Select(file => Entry(file)).ToArray()
        );

    private static ExportManifest Manifest(params ManifestEntry[] entries) =>
        new(ExportManifest.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, entries);

    private static ExportRequest Request(string filePath, ulong guildId, ulong channelId) =>
        new(
            new Guild(new Snowflake(guildId), "g", ""),
            new Channel(
                new Snowflake(channelId),
                ChannelKind.GuildTextChat,
                new Snowflake(guildId),
                null,
                "c",
                null,
                null,
                null,
                false,
                null
            ),
            filePath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

    [Fact]
    public void Strict_resume_matching_requires_the_same_channel_and_an_existing_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DceManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "archive.json");
        File.WriteAllText(filePath, "{}");

        try
        {
            var manifest = Manifest(
                Entry("archive.json", new FileInfo(filePath).Length, ComputeSha256(filePath))
            );

            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 2))
                .Should()
                .BeTrue();

            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 3))
                .Should()
                .BeFalse();

            File.Delete(filePath);
            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 2))
                .Should()
                .BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Strict_resume_matching_rejects_a_file_with_changed_size_or_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DceManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "archive.json");

        try
        {
            File.WriteAllText(filePath, "hello");
            var manifest = Manifest(
                Entry("archive.json", new FileInfo(filePath).Length, ComputeSha256(filePath))
            );
            var request = Request(filePath, guildId: 1, channelId: 2);

            File.WriteAllText(filePath, "hello world");
            ManifestResume.IsAlreadyExported(manifest, dir, request).Should().BeFalse();

            File.WriteAllText(filePath, "HELLO");
            ManifestResume.IsAlreadyExported(manifest, dir, request).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Strict_resume_matching_honors_cancellation_before_hashing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DceManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "archive.json");
        File.WriteAllText(filePath, "{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var manifest = Manifest(
                Entry("archive.json", new FileInfo(filePath).Length, ComputeSha256(filePath))
            );
            var request = Request(filePath, guildId: 1, channelId: 2);

            var act = () =>
                ManifestResume.IsAlreadyExported(manifest, dir, request, cancellation.Token);

            act.Should().Throw<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
