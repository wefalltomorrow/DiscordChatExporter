using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class ContinueExportRunnerTests
{
    private sealed class TestSnackbarManager : SnackbarManager
    {
        public override void Notify(string message, TimeSpan? duration = null) { }

        public override void Notify(
            string message,
            string actionText,
            Action actionHandler,
            TimeSpan? duration = null
        ) { }
    }

    private static DashboardViewModel CreateViewModel()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogManager>();
        services.AddSingleton<SnackbarManager, TestSnackbarManager>();
        services.AddSingleton<ViewManager>();
        services.AddSingleton<ViewModelManager>();
        services.AddSingleton(TestSettingsServiceFactory.Create());
        services.AddSingleton<UpdateService>();
        services.AddSingleton<LocalizationManager>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<ExportSetupViewModel>();
        services.AddTransient<MessageBoxViewModel>();
        services.AddTransient<SettingsViewModel>();

        var provider = services.BuildServiceProvider(true);
        return provider.GetRequiredService<DashboardViewModel>();
    }

    private static ResolvedContinueTarget Target(ulong channelId)
    {
        var guild = new Guild(new Snowflake(1), "Guild", "");
        var channel = new Channel(
            new Snowflake(channelId),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "channel-" + channelId,
            0,
            null,
            null,
            false,
            new Snowflake(channelId + 1000)
        );
        var dir = Path.Combine(Path.GetTempPath(), "DceContinueRunner_" + channelId);
        return new ResolvedContinueTarget(
            channel,
            guild,
            Path.Combine(dir, "export.json"),
            dir,
            ExportFormat.Json,
            new ContinuationCutoff(channel.Id, new Snowflake(channelId + 10), null, true, 1, true)
        );
    }

    [AvaloniaFact]
    public async Task Continuation_work_runs_off_the_ui_thread()
    {
        Dispatcher.UIThread.CheckAccess().Should().BeTrue();
        var ranOnUiThread = true;

        await DashboardViewModel.RunContinuationWorkOffUiThreadAsync(() =>
        {
            ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            return ValueTask.FromResult(42);
        });

        ranOnUiThread.Should().BeFalse();
    }

    [Fact]
    public async Task Nonfatal_failure_isolates_that_channel_and_others_still_run()
    {
        var viewModel = CreateViewModel();
        var targets = new[] { Target(10), Target(20), Target(30) };
        var observed = new List<Snowflake>();

        var summary = await viewModel.RunContinueLoopAsync(
            targets,
            (target, _) =>
            {
                observed.Add(target.Channel.Id);
                if (target.Channel.Id == new Snowflake(20))
                    throw new DiscordChatExporterException("nonfatal", isFatal: false);

                return Task.FromResult(new ContinueExportFileResult(true, 2, true));
            },
            CancellationToken.None
        );

        observed
            .Should()
            .Equal(targets[0].Channel.Id, targets[1].Channel.Id, targets[2].Channel.Id);
        summary.ProcessedCount.Should().Be(2);
        summary.TotalNewMessages.Should().Be(4);
        summary.FailedChannels.Should().ContainSingle().Which.Should().Be(targets[1].Channel);
    }

    [Fact]
    public async Task Fatal_failure_aborts_loop_and_skips_remaining()
    {
        var viewModel = CreateViewModel();
        var targets = new[] { Target(10), Target(20), Target(30) };
        var observed = new List<Snowflake>();

        var act = async () =>
            await viewModel.RunContinueLoopAsync(
                targets,
                (target, _) =>
                {
                    observed.Add(target.Channel.Id);
                    if (target.Channel.Id == new Snowflake(20))
                        throw new DiscordChatExporterException("fatal", isFatal: true);

                    return Task.FromResult(new ContinueExportFileResult(true, 1, true));
                },
                CancellationToken.None
            );

        await act.Should().ThrowAsync<DiscordChatExporterException>().Where(ex => ex.IsFatal);
        observed.Should().Equal(targets[0].Channel.Id, targets[1].Channel.Id);
    }

    [Fact]
    public async Task New_message_totals_summed_across_channels()
    {
        var viewModel = CreateViewModel();
        var targets = new[] { Target(10), Target(20), Target(30) };

        var summary = await viewModel.RunContinueLoopAsync(
            targets,
            (target, _) =>
                Task.FromResult(
                    target.Channel.Id == new Snowflake(20)
                        ? ContinueExportFileResult.Skipped
                        : new ContinueExportFileResult(
                            true,
                            (long)target.Channel.Id.Value / 10,
                            true
                        )
                ),
            CancellationToken.None
        );

        summary.ProcessedCount.Should().Be(2);
        summary.TotalNewMessages.Should().Be(4);
    }

    [Fact]
    public async Task SummarizeContinue_aggregates_processed_and_passes_through_failed()
    {
        var viewModel = CreateViewModel();
        var failed = Target(20);
        var targets = new[] { Target(10), failed, Target(30) };

        var summary = await viewModel.RunContinueLoopAsync(
            targets,
            (target, _) =>
            {
                if (target == failed)
                    throw new DiscordChatExporterException("nonfatal", isFatal: false);

                return Task.FromResult(new ContinueExportFileResult(true, 3, false));
            },
            CancellationToken.None
        );

        summary.ProcessedCount.Should().Be(2);
        summary.TotalNewMessages.Should().Be(6);
        summary.CatalogWriteFailed.Should().BeTrue();
        summary.FailedChannels.Should().Equal(failed.Channel);
    }

    [Fact]
    public void SummarizeContinue_empty_is_all_zero()
    {
        var summary = new ContinueExportRunSummary(0, 0, false, []);

        summary.ProcessedCount.Should().Be(0);
        summary.TotalNewMessages.Should().Be(0);
        summary.CatalogWriteFailed.Should().BeFalse();
        summary.FailedChannels.Should().BeEmpty();
    }

    [Fact]
    public void Up_to_date_when_processed_but_zero_new()
    {
        var viewModel = CreateViewModel();
        var summary = new ContinueExportRunSummary(1, 0, false, []);

        var message = viewModel.FormatContinueSummary(summary, skippedCount: 0);

        message.Should().Be(viewModel.LocalizationManager.ContinueExportUpToDateMessage);
    }

    [Fact]
    public void Reports_new_message_count_when_positive()
    {
        var viewModel = CreateViewModel();
        var failed = Target(20).Channel;
        var summary = new ContinueExportRunSummary(1, 3, true, [failed]);

        var message = viewModel.FormatContinueSummary(summary, skippedCount: 2);

        message.Should().Contain("Added 3 new message(s).");
        message.Should().Contain("2 skipped (no resumable export)");
        message.Should().Contain("1 failed");
        message.Should().Contain(viewModel.LocalizationManager.ExportCatalogWriteFailedMessage);
    }

    // Regression: a message-less (or otherwise unreadable) export must be skipped during the
    // up-front cutoff read, NOT abort the whole batch. This is the "selected export has nothing
    // to continue from" crash that aborted Continue when any one resolved export was empty.
    [Fact]
    public async Task Hydration_skips_an_empty_export_instead_of_aborting_the_batch()
    {
        var viewModel = CreateViewModel();
        var guild = new Guild(new Snowflake(1), "Guild", "");
        var good = MakeChannel(200);
        var empty = MakeChannel(300);

        var dir = Path.Combine(Path.GetTempPath(), "DceHydration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var goodPath = Path.Combine(dir, "good.json");
            var emptyPath = Path.Combine(dir, "empty.json");
            File.WriteAllText(
                goodPath,
                """{"guild":{"id":"1"},"channel":{"id":"200"},"messages":[{"id":"1000","timestamp":"2020-01-01T00:00:00.000+00:00"}]}"""
            );
            File.WriteAllText(
                emptyPath,
                """{"guild":{"id":"1"},"channel":{"id":"300"},"messages":[]}"""
            );

            var entries = new[]
            {
                new ResolvedCatalogEntry(good.Id, goodPath, ExportFormat.Json),
                new ResolvedCatalogEntry(empty.Id, emptyPath, ExportFormat.Json),
            };
            var byId = new Dictionary<Snowflake, Channel> { [good.Id] = good, [empty.Id] = empty };
            var unresolved = new List<UnresolvedCatalogChannel>();

            var targets = await viewModel.HydrateDiscoveredContinueTargetsAsync(
                entries,
                guild,
                byId,
                unresolved,
                TestContext.Current.CancellationToken
            );

            targets.Should().ContainSingle().Which.Channel.Id.Should().Be(good.Id);
            unresolved.Should().ContainSingle();
            unresolved[0].ChannelId.Should().Be(empty.Id);
            unresolved[0].Reason.Should().Be(ContinueSkipReason.CutoffUnreadable);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static Channel MakeChannel(ulong id) =>
        new(
            new Snowflake(id),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "channel-" + id,
            0,
            null,
            null,
            false,
            new Snowflake(id + 1000)
        );
}
