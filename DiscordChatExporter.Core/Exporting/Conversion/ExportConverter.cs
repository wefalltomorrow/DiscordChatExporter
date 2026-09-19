using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Core.Exporting.Conversion;

// Converts JSON exports to another offline export format without connecting to Discord.
public static class ExportConverter
{
    private sealed record MessageWithReferencedUsers(
        Message Message,
        IReadOnlyList<User> ReferencedUsers
    );

    public static async ValueTask<ExportResult> ConvertAsync(
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken cancellationToken = default
    )
    {
        if (targetFormat is ExportFormat.Json)
            throw new InvalidExportException("JSON is not a supported conversion target.");

        var parsed = await JsonExportReader.ParseAsync(jsonFilePath, cancellationToken);
        return await ConvertAsync(
            parsed,
            jsonFilePath,
            outputFilePath,
            targetFormat,
            cancellationToken
        );
    }

    public static async ValueTask<ExportResult> ConvertAsync(
        ParsedExport parsed,
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken cancellationToken = default
    )
    {
        if (targetFormat is ExportFormat.Json)
            throw new InvalidExportException("JSON is not a supported conversion target.");

        var messagesWithReferencedUsers = parsed
            .Messages.Select(message => new MessageWithReferencedUsers(
                message,
                message.GetReferencedUsers().DistinctBy(user => user.Id).ToArray()
            ))
            .ToArray();
        var referencedUsers = messagesWithReferencedUsers
            .SelectMany(message => message.ReferencedUsers)
            .DistinctBy(user => user.Id)
            .ToArray();
        var request = new ExportRequest(
            parsed.Guild,
            parsed.Channel,
            outputFilePath,
            null,
            targetFormat,
            parsed.After,
            parsed.Before,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: CultureInfo.InvariantCulture.Name,
            isUtcNormalizationEnabled: false
        );

        var context = new ExportContext(new DiscordClient("conversion-offline"), request, true);
        if (parsed.ConversionData is not null)
        {
            try
            {
                context.SeedFromConversionData(parsed.ConversionData, referencedUsers);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new InvalidExportException(
                    $"'{jsonFilePath}' contains malformed conversion metadata.",
                    ex
                );
            }
        }

        var exporter = new MessageExporter(context);
        try
        {
            foreach (var messageWithReferencedUsers in messagesWithReferencedUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var user in messageWithReferencedUsers.ReferencedUsers)
                    await context.PopulateMemberAsync(user, cancellationToken);

                await exporter.ExportMessageAsync(
                    messageWithReferencedUsers.Message,
                    cancellationToken
                );
            }
        }
        finally
        {
            await exporter.DisposeAsync();
        }

        return new ExportResult(exporter.Files, exporter.MessagesExported, 0);
    }
}
