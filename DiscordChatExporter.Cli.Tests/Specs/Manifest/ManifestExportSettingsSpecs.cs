using System;
using System.IO;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestExportSettingsSpecs
{
    private static Guild Guild() => new(new Snowflake(1), "Guild", "");

    private static Channel Channel() =>
        new(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "general",
            0,
            null,
            null,
            false,
            null
        );

    private static ExportRequest Request(
        bool markdown = true,
        bool downloadAssets = true,
        bool reuseAssets = true
    )
    {
        var output = Path.Combine(Path.GetTempPath(), "archive.json");
        var assets = Path.Combine(Path.GetTempPath(), "archive-assets");

        return new ExportRequest(
            Guild(),
            Channel(),
            output,
            assets,
            ExportFormat.Json,
            new Snowflake(10),
            new Snowflake(100),
            PartitionLimit.Null,
            MessageFilter.Parse("from:\"alice\" & has:file"),
            isReverseMessageOrder: false,
            shouldFormatMarkdown: markdown,
            shouldDownloadAssets: downloadAssets,
            shouldReuseAssets: reuseAssets,
            locale: "en-AU",
            isUtcNormalizationEnabled: true
        );
    }

    [Fact]
    public void Settings_round_trip_the_options_needed_for_continuation()
    {
        var original = Request();
        var settings = ManifestExportSettings.FromRequest(original);

        settings.ShouldDownloadAssets.Should().BeTrue();
        settings.ShouldReuseAssets.Should().BeTrue();
        settings.ShouldFormatMarkdown.Should().BeTrue();
        settings.MessageFilter.Should().Be("(from:\"alice\") & (has:file)");
        settings.Locale.Should().Be("en-AU");
        settings.IsUtcNormalizationEnabled.Should().BeTrue();
        settings.UsesDefaultAssetsDir.Should().BeFalse();

        var continued = settings.CreateContinuationRequest(
            original.Guild,
            original.Channel,
            original.OutputFilePath,
            Path.Combine(Path.GetTempPath(), "incremental.json"),
            original.Format,
            new Snowflake(90),
            before: null
        );

        continued.After.Should().Be(new Snowflake(90));
        continued.Before.Should().BeNull();
        continued.ShouldDownloadAssets.Should().BeTrue();
        continued.ShouldReuseAssets.Should().BeTrue();
        continued.ShouldFormatMarkdown.Should().BeTrue();
        continued.MessageFilter.ToExpression().Should().Be(settings.MessageFilter);
        continued.AssetsDirPath.Should().Be(original.AssetsDirPath);
        continued.Locale.Should().Be("en-AU");
        continued.IsUtcNormalizationEnabled.Should().BeTrue();
    }

    [Fact]
    public void Settings_restore_the_original_request_for_resume()
    {
        var original = Request();
        var settings = ManifestExportSettings.FromRequest(original);
        var restored = settings.CreateResumeRequest(
            original.Guild,
            original.Channel,
            original.OutputFilePath,
            original.Format
        );

        restored.OutputFilePath.Should().Be(original.OutputFilePath);
        restored.After.Should().Be(original.After);
        restored.Before.Should().Be(original.Before);
        restored.PartitionLimit.ToExpression().Should().Be(original.PartitionLimit.ToExpression());
        restored.MessageFilter.ToExpression().Should().Be(original.MessageFilter.ToExpression());
        restored.IsReverseMessageOrder.Should().Be(original.IsReverseMessageOrder);
        restored.ShouldFormatMarkdown.Should().Be(original.ShouldFormatMarkdown);
        restored.ShouldDownloadAssets.Should().Be(original.ShouldDownloadAssets);
        restored.ShouldReuseAssets.Should().Be(original.ShouldReuseAssets);
        restored.AssetsDirPath.Should().Be(original.AssetsDirPath);
        restored.HasExplicitAssetsDirPath.Should().BeTrue();
        restored.Locale.Should().Be(original.Locale);
        restored.IsUtcNormalizationEnabled.Should().Be(original.IsUtcNormalizationEnabled);
    }

    [Fact]
    public void Resume_restores_default_media_directory_relative_to_the_existing_archive()
    {
        var existingFilePath = Path.Combine(Path.GetTempPath(), "existing", "archive.json");
        var original = new ExportRequest(
            Guild(),
            Channel(),
            existingFilePath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: true,
            shouldReuseAssets: true,
            locale: "en-AU",
            isUtcNormalizationEnabled: true
        );
        var settings = ManifestExportSettings.FromRequest(original);

        var restored = settings.CreateResumeRequest(
            original.Guild,
            original.Channel,
            existingFilePath,
            original.Format
        );

        restored.HasExplicitAssetsDirPath.Should().BeFalse();
        restored
            .AssetsDirPath.Should()
            .Be($"{existingFilePath}_Files{Path.DirectorySeparatorChar}");
        restored.ShouldDownloadAssets.Should().BeTrue();
        restored.ShouldReuseAssets.Should().BeTrue();
    }

    [Fact]
    public void Compatibility_detects_changed_export_settings()
    {
        var original = Request(markdown: true);
        var settings = ManifestExportSettings.FromRequest(original);

        settings.IsCompatibleWith(original).Should().BeTrue();
        settings.IsCompatibleWith(Request(markdown: false)).Should().BeFalse();
    }

    [Theory]
    [InlineData("hello", "\"hello\"")]
    [InlineData("from:alice", "from:\"alice\"")]
    [InlineData("mentions:bob | has:image", "(mentions:\"bob\") | (has:image)")]
    [InlineData("~reaction:party", "~(reaction:\"party\")")]
    public void Filter_expressions_can_be_serialized_and_parsed_again(string input, string expected)
    {
        var filter = MessageFilter.Parse(input);
        var expression = filter.ToExpression();

        expression.Should().Be(expected);
        MessageFilter.Parse(expression!).Should().NotBeNull();
    }

    [Theory]
    [InlineData("100")]
    [InlineData("10mb")]
    public void Partition_expressions_can_be_serialized_and_parsed_again(string input)
    {
        var partition = PartitionLimit.Parse(input);
        var expression = partition.ToExpression();

        expression.Should().NotBeNullOrWhiteSpace();
        PartitionLimit.Parse(expression!).Should().NotBeNull();
    }
}
