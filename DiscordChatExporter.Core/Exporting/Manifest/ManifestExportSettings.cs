using System.IO;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Core.Exporting.Manifest;

// Settings required to reproduce an export when appending newer messages.
public sealed record ManifestExportSettings(
    string? AssetsDirPath,
    bool UsesDefaultAssetsDir,
    string? After,
    string? Before,
    string? PartitionLimit,
    string? MessageFilter,
    bool IsReverseMessageOrder,
    bool ShouldFormatMarkdown,
    bool ShouldDownloadAssets,
    bool ShouldReuseAssets,
    string? Locale,
    bool IsUtcNormalizationEnabled
)
{
    public static ManifestExportSettings FromRequest(ExportRequest request) =>
        new(
            request.HasExplicitAssetsDirPath ? request.AssetsDirPath : null,
            !request.HasExplicitAssetsDirPath,
            request.After?.ToString(),
            request.Before?.ToString(),
            request.PartitionLimit.ToExpression(),
            request.MessageFilter.ToExpression(),
            request.IsReverseMessageOrder,
            request.ShouldFormatMarkdown,
            request.ShouldDownloadAssets,
            request.ShouldReuseAssets,
            request.Locale,
            request.IsUtcNormalizationEnabled
        );

    public static ManifestExportSettings FromValues(
        string? assetsDirPath,
        Snowflake? after,
        Snowflake? before,
        string? partitionLimit,
        string? messageFilter,
        bool isReverseMessageOrder,
        bool shouldFormatMarkdown,
        bool shouldDownloadAssets,
        bool shouldReuseAssets,
        string? locale,
        bool isUtcNormalizationEnabled
    ) =>
        new(
            assetsDirPath,
            string.IsNullOrWhiteSpace(assetsDirPath),
            after?.ToString(),
            before?.ToString(),
            partitionLimit,
            messageFilter,
            isReverseMessageOrder,
            shouldFormatMarkdown,
            shouldDownloadAssets,
            shouldReuseAssets,
            locale,
            isUtcNormalizationEnabled
        );

    public bool IsCompatibleWith(ExportRequest request)
    {
        var current = FromRequest(request);

        return string.Equals(
                current.AssetsDirPath,
                AssetsDirPath,
                System.StringComparison.OrdinalIgnoreCase
            )
            && current.UsesDefaultAssetsDir == UsesDefaultAssetsDir
            && current.After == After
            && current.Before == Before
            && current.PartitionLimit == PartitionLimit
            && current.MessageFilter == MessageFilter
            && current.IsReverseMessageOrder == IsReverseMessageOrder
            && current.ShouldFormatMarkdown == ShouldFormatMarkdown
            && current.ShouldDownloadAssets == ShouldDownloadAssets
            && current.ShouldReuseAssets == ShouldReuseAssets
            && string.Equals(current.Locale, Locale, System.StringComparison.OrdinalIgnoreCase)
            && current.IsUtcNormalizationEnabled == IsUtcNormalizationEnabled;
    }

    public ExportRequest CreateResumeRequest(
        Guild guild,
        Channel channel,
        string existingFilePath,
        ExportFormat format
    )
    {
        var messageFilter = string.IsNullOrWhiteSpace(MessageFilter)
            ? Filtering.MessageFilter.Null
            : Filtering.MessageFilter.Parse(MessageFilter);

        var partitionLimit = string.IsNullOrWhiteSpace(PartitionLimit)
            ? Partitioning.PartitionLimit.Null
            : Partitioning.PartitionLimit.Parse(PartitionLimit);

        Snowflake? after = string.IsNullOrWhiteSpace(After) ? null : Snowflake.Parse(After);
        Snowflake? before = string.IsNullOrWhiteSpace(Before) ? null : Snowflake.Parse(Before);

        return new ExportRequest(
            guild,
            channel,
            existingFilePath,
            UsesDefaultAssetsDir ? null : AssetsDirPath,
            format,
            after,
            before,
            partitionLimit,
            messageFilter,
            IsReverseMessageOrder,
            ShouldFormatMarkdown,
            ShouldDownloadAssets,
            ShouldReuseAssets,
            Locale,
            IsUtcNormalizationEnabled
        );
    }

    public ExportRequest CreateContinuationRequest(
        Guild guild,
        Channel channel,
        string existingFilePath,
        string temporaryOutputPath,
        ExportFormat format,
        Snowflake after,
        Snowflake? before
    )
    {
        if (IsReverseMessageOrder)
        {
            throw new InvalidExportException(
                "Incremental continuation of reverse-chronological exports is not supported."
            );
        }

        if (!string.IsNullOrWhiteSpace(PartitionLimit))
        {
            throw new InvalidExportException(
                "Incremental continuation of partitioned exports is not supported."
            );
        }

        var messageFilter = string.IsNullOrWhiteSpace(MessageFilter)
            ? Filtering.MessageFilter.Null
            : Filtering.MessageFilter.Parse(MessageFilter);

        var assetsDirPath =
            ShouldDownloadAssets && UsesDefaultAssetsDir
                ? $"{existingFilePath}_Files{Path.DirectorySeparatorChar}"
                : AssetsDirPath;

        return new ExportRequest(
            guild,
            channel,
            temporaryOutputPath,
            assetsDirPath,
            format,
            after,
            before,
            Partitioning.PartitionLimit.Null,
            messageFilter,
            isReverseMessageOrder: false,
            ShouldFormatMarkdown,
            ShouldDownloadAssets,
            ShouldReuseAssets,
            Locale,
            IsUtcNormalizationEnabled
        );
    }
}
