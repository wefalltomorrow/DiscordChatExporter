using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Converters;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using Gress;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class ExportCommandBase : DiscordCommandBase
{
    private sealed class CliExportProgress(IProgress<Percentage> progress)
        : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => progress.Report(value.Fraction);
    }

    [CommandOption(
        "output",
        'o',
        Description = "Output file or directory path. "
            + "If a directory is specified, file names will be generated automatically based on the channel names and export parameters. "
            + "Directory paths must end with a slash to avoid ambiguity. "
            + "Supports template tokens, see the documentation for more info."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption("format", 'f', Description = "Export format.")]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.HtmlDark;

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date or message ID."
    )]
    public Snowflake? After { get; set; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date or message ID."
    )]
    public Snowflake? Before { get; set; }

    [CommandOption(
        "partition",
        'p',
        Description = "Split the output into partitions, each limited to the specified "
            + "number of messages (e.g. '100') or file size (e.g. '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; set; } = PartitionLimit.Null;

    [CommandOption(
        "include-threads",
        Description = "Which types of threads should be included.",
        Converter = typeof(ThreadInclusionModeInputConverter)
    )]
    public ThreadInclusionMode ThreadInclusionMode { get; set; } = ThreadInclusionMode.None;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter. "
            + "See the documentation for more info."
    )]
    public MessageFilter MessageFilter { get; set; } = MessageFilter.Null;

    [CommandOption(
        "parallel",
        Description = "Limits how many channels can be exported in parallel."
    )]
    public int ParallelLimit { get; set; } = 1;

    [CommandOption(
        "reverse",
        Description = "Export messages in reverse chronological order (newest first)."
    )]
    public bool IsReverseMessageOrder { get; set; }

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens."
    )]
    public bool ShouldFormatMarkdown { get; set; } = true;

    [CommandOption(
        "media",
        Description = "Download assets referenced by the export (user avatars, attached files, embedded images, etc.)."
    )]
    public bool ShouldDownloadAssets { get; set; }

    [CommandOption(
        "reuse-media",
        Description = "Reuse previously downloaded assets to avoid redundant requests."
    )]
    public bool ShouldReuseAssets { get; set; } = false;

    [CommandOption(
        "media-dir",
        Description = "Download assets to this directory. "
            + "If not specified, the asset directory path will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [CommandOption(
        "resume",
        Description = "Resume a previous multi-channel export by verifying manifest.json and skipping channels whose output is already complete. "
            + "Successful channels are checkpointed to the manifest as they finish."
    )]
    public bool ShouldResume { get; set; }

    [CommandOption(
        "checkpoint",
        Description = "Write or update manifest.json after each completed channel without skipping existing exports. "
            + "Use --resume on a later run to continue from those checkpoints."
    )]
    public bool ShouldCheckpoint { get; set; }

    [CommandOption(
        "dateformat",
        Description = "This option doesn't do anything. Kept for backwards compatibility."
    )]
    public string DateFormat { get; set; } = "MM/dd/yyyy h:mm tt";

    [CommandOption(
        "locale",
        Description = "Locale to use when formatting dates and numbers. "
            + "If not specified, the default system locale will be used."
    )]
    public string? Locale { get; set; }

    [CommandOption("utc", Description = "Normalize all timestamps to UTC+0.")]
    public bool IsUtcNormalizationEnabled { get; set; } = false;

    [CommandOption(
        "fuck-russia",
        EnvironmentVariable = "FUCK_RUSSIA",
        Description = "Don't print the Support Ukraine message to the console.",
        // Use a converter to accept '1' as 'true' to reuse the existing environment variable
        Converter = typeof(TruthyBooleanInputConverter)
    )]
    public bool IsUkraineSupportMessageDisabled { get; set; } = false;

    [field: AllowNull, MaybeNull]
    protected ChannelExporter Exporter => field ??= new ChannelExporter(Discord);

    private bool IsManifestCheckpointingEnabled => ShouldResume || ShouldCheckpoint;

    private static ManifestChannelInfo BuildManifestInfo(ExportRequest request) =>
        new(
            request.Guild.Id.ToString(),
            request.Guild.Name,
            request.Channel.Id.ToString(),
            request.Channel.Name,
            request.Channel.Parent?.Name,
            request.Format.ToString()
        );

    private static async ValueTask<string?> TryCheckpointManifestAsync(
        ExportRequest request,
        ExportResult result,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var entries = ManifestBuilder.Build(
                BuildManifestInfo(request),
                result,
                DateTimeOffset.Now,
                cancellationToken
            );

            await ManifestWriter.WriteAsync(
                request.OutputDirPath,
                entries,
                DateTimeOffset.Now,
                cancellationToken
            );

            return null;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    protected async ValueTask ExportAsync(IConsole console, IReadOnlyList<Channel> channels)
    {
        var cancellationToken = console.RegisterCancellationHandler();

        // Asset reuse can only be enabled if the download assets option is set
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/425
        if (ShouldReuseAssets && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --reuse-media cannot be used without --media.");
        }

        // Assets directory can only be specified if the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --media-dir cannot be used without --media.");
        }

        // Validate multi-channel output paths before fetching threads. Thread discovery can take
        // a long time, so fail early rather than after all of that work has completed.
        var mayExportMultipleChannels =
            channels.Count > 1 || ThreadInclusionMode != ThreadInclusionMode.None;

        var isEarlyOutputPathValid =
            !mayExportMultipleChannels
            || OutputPath.Contains('%')
            || Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath);

        if (!isEarlyOutputPathValid)
        {
            throw new CommandException(
                "Attempted to export multiple channels, but the output path is neither a directory nor a template. "
                    + "If the provided output path is meant to be treated as a directory, make sure it ends with a slash. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }

        var unwrappedChannels = new List<Channel>(channels);

        // Unwrap threads
        if (ThreadInclusionMode != ThreadInclusionMode.None)
        {
            await console.Output.WriteLineAsync("Fetching threads...");

            var fetchedThreadsCount = 0;
            await console
                .CreateStatusTicker()
                .StartAsync(
                    "...",
                    async ctx =>
                    {
                        await foreach (
                            var thread in Discord.GetChannelThreadsAsync(
                                channels,
                                ThreadInclusionMode == ThreadInclusionMode.All,
                                Before,
                                After,
                                cancellationToken
                            )
                        )
                        {
                            unwrappedChannels.Add(thread);

                            ctx.Status(Markup.Escape($"Fetched '{thread.GetHierarchicalName()}'."));

                            fetchedThreadsCount++;
                        }
                    }
                );

            // Remove forums, as they cannot be exported directly and their constituent threads
            // have already been fetched.
            unwrappedChannels.RemoveAll(channel => channel.Kind == ChannelKind.GuildForum);

            await console.Output.WriteLineAsync($"Fetched {fetchedThreadsCount} thread(s).");
        }

        if (unwrappedChannels.Count <= 0)
            throw new CommandException("No channels matched the provided export options.");

        var errorsByChannel = new ConcurrentDictionary<Channel, string>();
        var warningsByChannel = new ConcurrentDictionary<Channel, string>();
        var manifestWarningsByChannel = new ConcurrentDictionary<Channel, string>();
        var guildsById = new Dictionary<Snowflake, Guild>();
        var exportJobs = new List<ExportJob>();

        foreach (var channel in unwrappedChannels)
        {
            try
            {
                var guild = guildsById.GetValueOrDefault(channel.GuildId);
                if (guild is null)
                {
                    guild = await Discord.GetGuildAsync(channel.GuildId, cancellationToken);
                    guildsById[channel.GuildId] = guild;
                }

                exportJobs.Add(
                    new ExportJob(
                        channel,
                        new ExportRequest(
                            guild,
                            channel,
                            OutputPath,
                            AssetsDirPath,
                            ExportFormat,
                            After,
                            Before,
                            PartitionLimit,
                            MessageFilter,
                            IsReverseMessageOrder,
                            ShouldFormatMarkdown,
                            ShouldDownloadAssets,
                            ShouldReuseAssets,
                            Locale,
                            IsUtcNormalizationEnabled
                        )
                    )
                );
            }
            catch (DiscordChatExporterException ex) when (!ex.IsFatal)
            {
                errorsByChannel[channel] = ex.Message;
            }
        }

        var duplicateOutputPaths = ExportOutputPathValidator.GetDuplicateOutputFilePaths(
            exportJobs.Select(j => j.Request)
        );
        if (duplicateOutputPaths.Count > 0)
        {
            throw new CommandException(
                "Multiple channels would be exported to the same output file. "
                    + "Use a directory path or include a unique template token such as %c. "
                    + "Conflicting output path(s): "
                    + string.Join(", ", duplicateOutputPaths)
            );
        }

        var skippedCompletedCount = 0;

        if (ShouldResume && exportJobs.Count > 0)
        {
            var manifestsByDir = new Dictionary<string, ExportManifest?>(
                StringComparer.OrdinalIgnoreCase
            );
            var pendingJobs = new List<ExportJob>(exportJobs.Count);

            foreach (var job in exportJobs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dirPath = job.Request.OutputDirPath;
                if (!manifestsByDir.TryGetValue(dirPath, out var manifest))
                {
                    manifest = await ManifestReader.TryReadAsync(
                        Path.Combine(dirPath, ExportManifest.FileName),
                        cancellationToken
                    );
                    manifestsByDir[dirPath] = manifest;
                }

                if (
                    ManifestResume.IsAlreadyExported(
                        manifest,
                        dirPath,
                        job.Request,
                        cancellationToken
                    )
                )
                {
                    skippedCompletedCount++;
                }
                else
                {
                    pendingJobs.Add(job);
                }
            }

            exportJobs = pendingJobs;

            if (skippedCompletedCount > 0)
            {
                await console.Output.WriteLineAsync(
                    $"Skipping {skippedCompletedCount} already-completed channel(s)."
                );
            }
        }

        if (exportJobs.Count <= 0)
        {
            if (skippedCompletedCount > 0 && errorsByChannel.IsEmpty)
            {
                await console.Output.WriteLineAsync(
                    "All selected channels are already complete and verified by manifest.json."
                );
                return;
            }

            if (errorsByChannel.Count >= unwrappedChannels.Count)
                throw new CommandException("Export failed.");
        }

        // Export
        await console.Output.WriteLineAsync($"Exporting {exportJobs.Count} channel(s)...");
        await console
            .CreateProgressTicker()
            .HideCompleted(
                // When exporting multiple channels in parallel, hide the completed tasks
                // because it gets hard to visually parse them as they complete out of order.
                // https://github.com/Tyrrrz/DiscordChatExporter/issues/1124
                ParallelLimit > 1
            )
            .StartAsync(async ctx =>
            {
                await Parallel.ForEachAsync(
                    exportJobs,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                        CancellationToken = cancellationToken,
                    },
                    async (job, innerCancellationToken) =>
                    {
                        var channel = job.Channel;
                        try
                        {
                            await ctx.StartTaskAsync(
                                Markup.Escape(channel.GetHierarchicalName()),
                                async progress =>
                                {
                                    var percentageProgress = progress.ToPercentageBased();
                                    var result = await Exporter.ExportChannelAsync(
                                        job.Request,
                                        new CliExportProgress(percentageProgress),
                                        innerCancellationToken
                                    );

                                    if (IsManifestCheckpointingEnabled)
                                    {
                                        var manifestWarning = await TryCheckpointManifestAsync(
                                            job.Request,
                                            result,
                                            innerCancellationToken
                                        );

                                        if (!string.IsNullOrWhiteSpace(manifestWarning))
                                            manifestWarningsByChannel[channel] = manifestWarning;
                                    }
                                }
                            );
                        }
                        catch (ChannelEmptyException ex)
                        {
                            warningsByChannel[channel] = ex.Message;

                            if (IsManifestCheckpointingEnabled)
                            {
                                var manifestWarning = await TryCheckpointManifestAsync(
                                    job.Request,
                                    new ExportResult(
                                        [
                                            new ExportedFile(
                                                job.Request.OutputFilePath,
                                                0,
                                                null,
                                                null,
                                                null,
                                                null
                                            ),
                                        ],
                                        0,
                                        0
                                    ),
                                    innerCancellationToken
                                );

                                if (!string.IsNullOrWhiteSpace(manifestWarning))
                                    manifestWarningsByChannel[channel] = manifestWarning;
                            }
                        }
                        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                        {
                            errorsByChannel[channel] = ex.Message;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Output failures such as a locked file or full disk should fail only
                            // this channel, not tear down an otherwise resumable batch.
                            errorsByChannel[channel] = ex.Message;
                        }
                    }
                );
            });

        // Print the result. Some errors may have happened while building requests for
        // channels that never became export jobs, so count attempted failures separately.
        var attemptedChannels = exportJobs.Select(job => job.Channel).ToHashSet();
        var attemptedErrorCount = errorsByChannel.Keys.Count(attemptedChannels.Contains);
        var successfulThisRunCount = exportJobs.Count - attemptedErrorCount;

        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully exported {Math.Max(0, successfulThisRunCount)} channel(s)."
            );

            if (skippedCompletedCount > 0)
            {
                await console.Output.WriteLineAsync(
                    $"Resumed past {skippedCompletedCount} previously completed channel(s)."
                );
            }
        }

        // Print warnings
        if (warningsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Yellow))
            {
                await console.Error.WriteLineAsync(
                    "Warnings reported for the following channel(s):"
                );
            }

            foreach (var (channel, message) in warningsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        if (manifestWarningsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Yellow))
            {
                await console.Error.WriteLineAsync(
                    "Manifest checkpoint warnings were reported for the following channel(s):"
                );
            }

            foreach (var (channel, message) in manifestWarningsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Yellow))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Print errors
        if (errorsByChannel.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to export the following channel(s):");
            }

            foreach (var (channel, message) in errorsByChannel)
            {
                await console.Error.WriteAsync($"{channel.GetHierarchicalName()}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Fail the command only if ALL channels failed to export.
        // If only some channels failed to export, it's okay.
        if (exportJobs.Count > 0 && attemptedErrorCount >= exportJobs.Count)
            throw new CommandException("Export failed.");
    }

    private sealed record ExportJob(Channel Channel, ExportRequest Request);

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        // Support Ukraine callout
        if (!IsUkraineSupportMessageDisabled)
        {
            console.Output.WriteLine(
                "┌────────────────────────────────────────────────────────────────────┐"
            );
            console.Output.WriteLine(
                "│   Thank you for supporting Ukraine <3                              │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   As Russia wages a genocidal war against my country,              │"
            );
            console.Output.WriteLine(
                "│   I'm grateful to everyone who continues to                        │"
            );
            console.Output.WriteLine(
                "│   stand with Ukraine in our fight for freedom.                     │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   Learn more: https://tyrrrz.me/ukraine                            │"
            );
            console.Output.WriteLine(
                "└────────────────────────────────────────────────────────────────────┘"
            );
            console.Output.WriteLine("");
        }

        await base.ExecuteAsync(console);
    }
}
