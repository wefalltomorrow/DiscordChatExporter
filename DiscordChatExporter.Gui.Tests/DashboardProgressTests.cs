using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Gress;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DashboardProgressTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<DialogManager>();
        services.AddSingleton<SnackbarManager>();
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

        return services.BuildServiceProvider(true);
    }

    private static DashboardViewModel CreateViewModel()
    {
        var provider = BuildServices();
        return provider.GetRequiredService<DashboardViewModel>();
    }

    private static void Invoke(
        DashboardViewModel viewModel,
        string methodName,
        params object?[] args
    )
    {
        var method = typeof(DashboardViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        method.Should().NotBeNull();
        method!.Invoke(viewModel, args);
    }

    [AvaloniaFact]
    public void Progress_bar_uses_muxer_fraction_without_count_estimates()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", 1);
        viewModel.Progress.Report(Percentage.FromFraction(0.25));
        Dispatcher.UIThread.RunJobs();

        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.9), 5, DateTimeOffset.UnixEpoch)
        );

        viewModel.DisplayedProgressFraction.Should().BeApproximately(0.25, 0.0001);
        viewModel.MessagesReadText.Should().Be("5 messages");
        viewModel.MessagesReadText.Should().NotContain("left");
    }

    [AvaloniaFact]
    public void Completed_run_reports_finished()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", 1);
        viewModel.Progress.Report(Percentage.FromFraction(0.2));
        Dispatcher.UIThread.RunJobs();

        Invoke(viewModel, "MarkExportProgressCompleted", 0);

        viewModel.DisplayedProgressFraction.Should().Be(1);
    }

    [AvaloniaFact]
    public void Channel_progress_advances_across_multiple_channels()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", 2);
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.5), 2, DateTimeOffset.UnixEpoch)
        );

        viewModel.ChannelProgressText.Should().Be("Channel 1 of 2");

        Invoke(viewModel, "MarkExportProgressCompleted", 0);

        viewModel.ChannelProgressText.Should().Be("Channel 2 of 2");
    }

    [AvaloniaFact]
    public void Progress_status_shows_latest_exported_month()
    {
        var viewModel = CreateViewModel();

        Invoke(viewModel, "StartExportProgressRun", 2);
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(
                Percentage.FromFraction(0.5),
                1,
                new DateTimeOffset(2024, 02, 03, 0, 0, 0, TimeSpan.Zero)
            )
        );

        viewModel.ExportedThroughText.Should().Be("exported through Feb 2024");
    }

    [AvaloniaFact]
    public async Task Background_completion_updates_bound_properties_on_ui_thread()
    {
        var viewModel = CreateViewModel();
        var uiThreadId = Environment.CurrentManagedThreadId;
        var progressChangedThreadIds = new List<int>();

        Invoke(viewModel, "StartExportProgressRun", 1);
        Invoke(
            viewModel,
            "ApplyExportProgress",
            0,
            new ExportProgress(Percentage.FromFraction(0.2), 2, DateTimeOffset.UnixEpoch)
        );

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DashboardViewModel.DisplayedProgressFraction))
                progressChangedThreadIds.Add(Environment.CurrentManagedThreadId);
        };

        // Complete the run from a background thread, then pump the dispatcher to apply the posted update.
        await Task.Run(() => Invoke(viewModel, "MarkExportProgressCompleted", 0));
        Dispatcher.UIThread.RunJobs();

        // The contract this test guards: a background-thread completion is marshaled *to the UI thread*,
        // never applied on the background thread. The thread the mutation fired on captures that directly
        // and is timing-independent. We deliberately do NOT assert the fraction is still 0 between the
        // post and the pump: once the job is posted, any pump of the process-global headless dispatcher
        // (framework internals, or other tests sharing it in a full-suite run) may run it first, which
        // made that intermediate assertion flaky.
        viewModel.DisplayedProgressFraction.Should().Be(1);
        progressChangedThreadIds.Should().ContainSingle().Which.Should().Be(uiThreadId);
    }

    [AvaloniaFact]
    public void Overlapping_rate_limit_pauses_clear_only_after_all_pauses_resume()
    {
        var viewModel = CreateViewModel();

        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(true, TimeSpan.FromSeconds(5))
        );
        Invoke(
            viewModel,
            "HandleRateLimitChanged",
            null,
            new RateLimitState(true, TimeSpan.FromSeconds(10))
        );
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeTrue();

        Invoke(viewModel, "HandleRateLimitChanged", null, new RateLimitState(false, TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeTrue();

        Invoke(viewModel, "HandleRateLimitChanged", null, new RateLimitState(false, TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();

        viewModel.IsRateLimitPaused.Should().BeFalse();
    }
}
