using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Onova;
using Onova.Models;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class MainViewUpdateTests
{
    private sealed class RecordingSnackbarManager : SnackbarManager
    {
        public List<string> Messages { get; } = [];
        public int ActionNotificationCount { get; private set; }

        public override void Notify(string message, TimeSpan? duration = null) =>
            Messages.Add(message);

        public override void Notify(
            string message,
            string actionText,
            Action actionHandler,
            TimeSpan? duration = null
        )
        {
            Messages.Add(message);
            ActionNotificationCount++;
        }
    }

    private sealed class FailingPrepareUpdateManager : IUpdateManager
    {
        private static readonly Version AvailableVersion = new(99, 0);

        public AssemblyMetadata Updatee { get; } =
            new("DiscordChatExporter", new Version(1, 0), "DiscordChatExporter.exe");

        public bool IsUpdatePrepared(Version version) => false;

        public IReadOnlyList<Version> GetPreparedUpdates() => [];

        public Task<CheckForUpdatesResult> CheckForUpdatesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CheckForUpdatesResult([AvailableVersion], AvailableVersion, true));

        public Task PrepareUpdateAsync(
            Version version,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default
        ) => throw new IOException("prepare failed");

        public void LaunchUpdater(
            Version version,
            bool needRestart = false,
            string? executablePath = null
        ) { }

        public void Dispose() { }
    }

    private sealed class FailingSecondPrepareUpdateManager : IUpdateManager
    {
        private int _prepareAttemptCount;

        public List<Version> LaunchedVersions { get; } = [];

        public AssemblyMetadata Updatee { get; } =
            new("DiscordChatExporter", new Version(1, 0), "DiscordChatExporter.exe");

        public bool IsUpdatePrepared(Version version) => _prepareAttemptCount == 1;

        public IReadOnlyList<Version> GetPreparedUpdates() => [];

        public Task<CheckForUpdatesResult> CheckForUpdatesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CheckForUpdatesResult([], null, false));

        public Task PrepareUpdateAsync(
            Version version,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default
        )
        {
            _prepareAttemptCount++;
            if (_prepareAttemptCount > 1)
                throw new IOException("prepare failed");

            return Task.CompletedTask;
        }

        public void LaunchUpdater(
            Version version,
            bool needRestart = false,
            string? executablePath = null
        ) => LaunchedVersions.Add(version);

        public void Dispose() { }
    }

    private static ServiceProvider BuildServices(RecordingSnackbarManager snackbarManager)
    {
        var services = new ServiceCollection();
        var settingsService = new SettingsService
        {
            IsUkraineSupportMessageEnabled = false,
            IsAutoUpdateEnabled = true,
        };

        services.AddSingleton<DialogManager>();
        services.AddSingleton<SnackbarManager>(snackbarManager);
        services.AddSingleton<ViewManager>();
        services.AddSingleton<ViewModelManager>();
        services.AddSingleton(settingsService);
        services.AddSingleton(
            new UpdateService(settingsService, new FailingPrepareUpdateManager())
        );
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

    [Fact]
    public async Task Failed_update_prepare_does_not_show_update_ready_action()
    {
        var snackbarManager = new RecordingSnackbarManager();
        using var provider = BuildServices(snackbarManager);
        var viewModel = provider.GetRequiredService<MainViewModel>();
        var localizationManager = provider.GetRequiredService<LocalizationManager>();
        var method = typeof(MainViewModel).GetMethod(
            "CheckForUpdatesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        await (Task)method!.Invoke(viewModel, null)!;

        snackbarManager.Messages.Should().Contain(localizationManager.UpdateFailedMessage);
        snackbarManager.Messages.Should().NotContain(localizationManager.UpdateReadyMessage);
        snackbarManager.ActionNotificationCount.Should().Be(0);
    }

    [Fact]
    public async Task Failed_update_prepare_clears_previous_prepared_update()
    {
        var settingsService = new SettingsService { IsAutoUpdateEnabled = true };
        var updateManager = new FailingSecondPrepareUpdateManager();
        var updateService = new UpdateService(settingsService, updateManager);

        (await updateService.PrepareUpdateAsync(new Version(2, 0))).Should().BeTrue();
        await FluentActions
            .Awaiting(() => updateService.PrepareUpdateAsync(new Version(3, 0)).AsTask())
            .Should()
            .ThrowAsync<IOException>();

        updateService.FinalizeUpdate(false).Should().BeFalse();
        updateManager.LaunchedVersions.Should().BeEmpty();
    }
}
