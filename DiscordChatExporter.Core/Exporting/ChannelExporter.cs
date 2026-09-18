using System;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

public class ChannelExporter(DiscordClient discord)
{
    public async ValueTask<ExportResult> ExportChannelAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.Kind == ChannelKind.GuildForum)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        // Build context
        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        // Initialize the exporter before further checks to ensure the file is created even if
        // an exception is thrown after this point.
        var messageExporter = new MessageExporter(context);
        Exception? exportException = null;
        try
        {
            // Check if the channel is empty
            if (request.Channel.IsEmpty)
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages; an empty file will be created."
                );
            }

            // Check if the 'before' and 'after' boundaries are valid
            if (
                (
                    request.Before is not null
                    && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
                )
                || (
                    request.After is not null
                    && !request.Channel.MayHaveMessagesAfter(request.After.Value)
                )
            )
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages within the specified period; an empty file will be created."
                );
            }

            var progressState = new ExportProgressState();

            var messages = !request.IsReverseMessageOrder
                ? discord.GetMessagesAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progressState,
                    cancellationToken
                )
                : discord.GetMessagesInReverseAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progressState,
                    cancellationToken
                );

            await foreach (var message in messages)
            {
                progressState.ReportWalkedMessage(message, progress);

                try
                {
                    // Resolve members for referenced users
                    foreach (var user in message.GetReferencedUsers())
                        await context.PopulateMemberAsync(user, cancellationToken);

                    // Export the message
                    if (request.MessageFilter.IsMatch(message))
                        await messageExporter.ExportMessageAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Provide more context to the exception, to simplify debugging based on error messages
                    throw new DiscordChatExporterException(
                        $"Failed to export message #{message.Id} "
                            + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                            + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                        ex is not DiscordChatExporterException dex || dex.IsFatal,
                        ex
                    );
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            exportException = ex;
            throw;
        }
        finally
        {
            if (exportException is null or ChannelEmptyException)
            {
                await messageExporter.DisposeAsync(cancellationToken);
            }
            else
            {
                try
                {
                    await messageExporter.AbortAsync();
                }
                catch
                {
                    // Preserve the original export exception.
                }
            }
        }

        return new ExportResult(
            messageExporter.Files,
            messageExporter.MessagesExported,
            context.DownloadedAssetCount
        );
    }

    internal sealed class ExportProgressState : IProgress<Percentage>
    {
        private Percentage _currentFraction = Percentage.FromFraction(0);
        private long _messagesRead;

        public void Report(Percentage value) => _currentFraction = value;

        public void ReportWalkedMessage(Message message, IProgress<ExportProgress>? progress) =>
            ChannelExporter.ReportWalkedMessage(
                message,
                _currentFraction,
                progress,
                ref _messagesRead
            );
    }

    internal static void ReportWalkedMessage(
        Message message,
        Percentage fraction,
        IProgress<ExportProgress>? progress,
        ref long messagesRead
    )
    {
        messagesRead++;
        progress?.Report(new ExportProgress(fraction, messagesRead, message.Timestamp));
    }
}
