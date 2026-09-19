using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class LibraryViewModelTests
{
    private sealed class RecordingSnackbarManager : SnackbarManager
    {
        public List<string> Messages { get; } = [];

        public override void Notify(string message, TimeSpan? duration = null) =>
            Messages.Add(message);
    }

    [AvaloniaFact]
    public async Task Blocking_search_work_runs_off_the_ui_thread()
    {
        Dispatcher.UIThread.CheckAccess().Should().BeTrue();
        var ranOnUiThread = true;

        await LibraryViewModel.RunSearchOffUiThreadAsync(() =>
        {
            ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            return ValueTask.FromResult<IReadOnlyList<SqliteSearchHit>>([]);
        });

        ranOnUiThread.Should().BeFalse();
    }

    [Fact]
    public void Search_is_disabled_while_the_library_is_busy()
    {
        var viewModel = new LibraryViewModel(
            new SettingsService(),
            new DialogManager(),
            new SnackbarManager(),
            new LocalizationManager(new SettingsService()),
            () => { }
        )
        {
            HasSearchableExports = true,
            IsBusy = true,
        };

        viewModel.SearchCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Scan_folder_reports_settings_save_failure_without_throwing()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "DceLibraryScan_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        var snackbarManager = new RecordingSnackbarManager();
        var settingsService = new SettingsService();
        var viewModel = new LibraryViewModel(
            settingsService,
            new DialogManager(),
            snackbarManager,
            new LocalizationManager(settingsService),
            () => throw new IOException("settings write failed")
        );

        try
        {
            var act = async () => await viewModel.ScanFolderAsync(dir);

            await act.Should().NotThrowAsync();
            snackbarManager.Messages.Should().Contain("settings write failed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
