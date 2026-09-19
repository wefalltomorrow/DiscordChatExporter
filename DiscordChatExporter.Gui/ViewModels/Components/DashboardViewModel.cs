using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Models;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.Utils;
using DiscordChatExporter.Gui.Utils.Extensions;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using Gress;
using Gress.Completable;
using PowerKit;
using PowerKit.Extensions;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly record struct ExportProgressSnapshot(
        long MessagesRead,
        DateTimeOffset? CurrentTimestamp,
        int CompletedChannelCount,
        int ChannelCount,
        bool IsRunCompleted
    );

    private readonly ViewModelManager _viewModelManager;
    private readonly SnackbarManager _snackbarManager;
    private readonly DialogManager _dialogManager;
    private readonly SettingsService _settingsService;

    private readonly IDisposable _eventSubscription;
    private readonly AutoResetProgressMuxer _progressMuxer;
    private readonly object _exportProgressLock = new();

    private long[] _messagesReadByChannel = [];
    private DateTimeOffset?[] _currentTimestampByChannel = [];
    private bool[] _completedChannels = [];
    private long _lastRateMessagesRead;
    private double _messageRate;
    private DateTimeOffset? _lastRateUpdate;
    private int _activeRateLimitPauseCount;

    private DiscordClient? _discord;
    private IReadOnlyList<ChannelConnection> _exportableChannels = [];

    private ExportSetupViewModel? _lastExportSetup;
    private IReadOnlyList<Channel> _lastFailedChannels = [];
    private CancellationTokenSource? _operationCancellation;

    public DashboardViewModel(
        ViewModelManager viewModelManager,
        DialogManager dialogManager,
        SnackbarManager snackbarManager,
        SettingsService settingsService,
        LocalizationManager localizationManager
    )
    {
        _viewModelManager = viewModelManager;
        _dialogManager = dialogManager;
        _snackbarManager = snackbarManager;
        _settingsService = settingsService;
        LocalizationManager = localizationManager;

        _progressMuxer = Progress.CreateMuxer().WithAutoReset();

        _eventSubscription = Disposable.Merge(
            Progress.WatchProperty(
                o => o.Current,
                _ =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        DisplayedProgressFraction = Progress.Current.Fraction;
                        OnPropertyChanged(nameof(IsProgressIndeterminate));
                    })
            ),
            SelectedChannels.WatchProperty(
                o => o.Count,
                _ =>
                {
                    ExportCommand.NotifyCanExecuteChanged();
                    ContinueExportCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(AllChannelsSelected));
                    OnPropertyChanged(nameof(SelectAllChannelsButtonText));
                }
            )
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateToLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateToConversionCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial double DisplayedProgressFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? MessagesReadText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? RateText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? ExportedThroughText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? ChannelProgressText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(IsRateLimitPaused))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? RateLimitPauseText { get; set; }

    public bool HasProgressStatus =>
        !string.IsNullOrEmpty(MessagesReadText)
        || !string.IsNullOrEmpty(RateText)
        || !string.IsNullOrEmpty(ExportedThroughText)
        || !string.IsNullOrEmpty(ChannelProgressText);

    public bool IsRateLimitPaused => !string.IsNullOrEmpty(RateLimitPauseText);

    public bool HasProgressDisplay => HasProgressStatus || IsRateLimitPaused;

    public LocalizationManager LocalizationManager { get; }

    public bool CanCancelOperation => _operationCancellation is { IsCancellationRequested: false };

    internal CancellationToken BeginCancelableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
        return _operationCancellation.Token;
    }

    internal void EndCancelableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    internal static Task<T> RunContinuationWorkOffUiThreadAsync<T>(
        Func<ValueTask<T>> workAsync,
        CancellationToken cancellationToken = default
    ) => Task.Run(async () => await workAsync(), cancellationToken);

    public ProgressContainer<Percentage> Progress { get; } = new();

    public bool IsProgressIndeterminate =>
        IsBusy && (IsRateLimitPaused || DisplayedProgressFraction is <= 0 or >= 1);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    public partial string? Token { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Guild>? AvailableGuilds { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
    public partial Guild? SelectedGuild { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllChannelsSelected))]
    [NotifyPropertyChangedFor(nameof(SelectAllChannelsButtonText))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllChannelsCommand))]
    public partial IReadOnlyList<ChannelConnection>? AvailableChannels { get; set; }

    public ObservableCollection<ChannelConnection> SelectedChannels { get; } = [];

    partial void OnAvailableChannelsChanged(IReadOnlyList<ChannelConnection>? value)
    {
        _exportableChannels = value is not null ? FlattenExportableChannels(value).ToArray() : [];
    }

    // True when every exportable (non-category) channel in the current guild is selected.
    public bool AllChannelsSelected
    {
        get
        {
            var exportableCount = _exportableChannels.Count;
            return exportableCount > 0 && SelectedChannels.Count >= exportableCount;
        }
    }

    public string SelectAllChannelsButtonText =>
        AllChannelsSelected
            ? LocalizationManager.DeselectAllChannelsButton
            : LocalizationManager.SelectAllChannelsButton;

    public override Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.LastToken))
            Token = _settingsService.LastToken;

        return Task.CompletedTask;
    }

    private void SetDiscordClient(DiscordClient discord)
    {
        var previous = _discord;
        if (previous is not null)
            previous.RateLimitChanged -= HandleRateLimitChanged;

        _discord = discord;
        _discord.RateLimitChanged += HandleRateLimitChanged;

        previous?.Dispose();
    }

    private void HandleRateLimitChanged(object? sender, RateLimitState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (state.IsPaused)
            {
                _activeRateLimitPauseCount++;
                RateLimitPauseText = string.Format(
                    LocalizationManager.RateLimitPauseFormat,
                    Math.Ceiling(state.Remaining.TotalSeconds)
                        .ToString("N0", CultureInfo.CurrentCulture)
                );
            }
            else
            {
                _activeRateLimitPauseCount = Math.Max(0, _activeRateLimitPauseCount - 1);
                if (_activeRateLimitPauseCount <= 0)
                    RateLimitPauseText = null;
            }

            OnPropertyChanged(nameof(IsProgressIndeterminate));
        });
    }

    [RelayCommand]
    private async Task ShowSettingsAsync() =>
        await _dialogManager.ShowDialogAsync(_viewModelManager.GetSettingsViewModel());

    // Raised when the user opens the Library from the Dashboard. MainViewModel handles the switch.
    public event EventHandler? LibraryRequested;

    public event EventHandler? ConversionRequested;

    private bool CanNavigate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToLibrary() => LibraryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToConversion() => ConversionRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    private bool CanPullGuilds() => !IsBusy && !string.IsNullOrWhiteSpace(Token);

    [RelayCommand(CanExecute = nameof(CanPullGuilds))]
    private async Task PullGuildsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            var token = Token?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(token))
                return;

            AvailableGuilds = null;
            SelectedGuild = null;
            AvailableChannels = null;
            SelectedChannels.Clear();
            ClearFailedExportState();

            var discord = new DiscordClient(token, _settingsService.RateLimitPreference);
            SetDiscordClient(discord);
            _settingsService.LastToken = token;

            var guilds = await discord.GetUserGuildsAsync();

            AvailableGuilds = guilds;
            SelectedGuild = guilds.FirstOrDefault();

            await PullChannelsAsync();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorPullingGuildsTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private bool CanPullChannels() => !IsBusy && _discord is not null && SelectedGuild is not null;

    [RelayCommand(CanExecute = nameof(CanPullChannels))]
    private async Task PullChannelsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            if (_discord is null || SelectedGuild is null)
                return;

            AvailableChannels = null;
            SelectedChannels.Clear();
            ClearFailedExportState();

            var channels = new List<Channel>();

            // Regular channels
            await foreach (var channel in _discord.GetGuildChannelsAsync(SelectedGuild.Id))
                channels.Add(channel);

            // Threads
            if (_settingsService.ThreadInclusionMode != ThreadInclusionMode.None)
            {
                await foreach (
                    var thread in _discord.GetGuildThreadsAsync(
                        SelectedGuild.Id,
                        _settingsService.ThreadInclusionMode == ThreadInclusionMode.All
                    )
                )
                {
                    channels.Add(thread);
                }
            }

            // Build a hierarchy of channels
            var channelTree = ChannelConnection.BuildTree(
                channels
                    .OrderByDescending(c => c.IsDirect ? c.LastMessageId : null)
                    .ThenBy(c => c.Position)
                    .ToArray()
            );

            AvailableChannels = channelTree;
            SelectedChannels.Clear();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorPullingChannelsTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    // Walks the channel tree and yields every exportable (non-category) channel,
    // including nested threads. Categories are not exportable and are skipped.
    private static IEnumerable<ChannelConnection> FlattenExportableChannels(
        IReadOnlyList<ChannelConnection> connections
    )
    {
        foreach (var connection in connections)
        {
            if (!connection.Channel.IsCategory)
                yield return connection;

            if (connection.Children.Count > 0)
            {
                foreach (var child in FlattenExportableChannels(connection.Children))
                    yield return child;
            }
        }
    }

    private bool CanSelectAllChannels() => !IsBusy && _exportableChannels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSelectAllChannels))]
    private void SelectAllChannels()
    {
        if (_exportableChannels.Count <= 0)
            return;

        // Capture the toggle state before mutating, since AllChannelsSelected
        // is derived from SelectedChannels.Count and would flip after Clear().
        var wasAllSelected = AllChannelsSelected;
        SelectedChannels.Clear();

        if (!wasAllSelected)
        {
            foreach (var connection in _exportableChannels)
                SelectedChannels.Add(connection);
        }
    }

    private Channel[] GetSelectedExportableChannels() =>
        SelectedChannels.Where(c => !c.Channel.IsCategory).Select(c => c.Channel).ToArray();

    private bool HasSelectedExportableChannels() =>
        SelectedChannels.Any(c => !c.Channel.IsCategory);

    private bool CanExport() =>
        !IsBusy
        && _discord is not null
        && SelectedGuild is not null
        && HasSelectedExportableChannels();

    private static async ValueTask SetClipboardTextAsync(string text)
    {
        var clipboard =
            Avalonia.Application.Current?.ApplicationLifetime?.TryGetTopLevel()?.Clipboard
            ?? throw new ApplicationException("Could not access the clipboard.");

        await clipboard.SetTextAsync(text);
    }

    private void ResetExportProgressDisplay()
    {
        lock (_exportProgressLock)
        {
            _messagesReadByChannel = [];
            _currentTimestampByChannel = [];
            _completedChannels = [];
            _lastRateMessagesRead = 0;
            _messageRate = 0;
            _lastRateUpdate = null;
        }

        _activeRateLimitPauseCount = 0;
        DisplayedProgressFraction = 0;
        MessagesReadText = null;
        RateText = null;
        ExportedThroughText = null;
        ChannelProgressText = null;
        RateLimitPauseText = null;
    }

    private void StartExportProgressRun(int channelCount)
    {
        lock (_exportProgressLock)
        {
            _messagesReadByChannel = new long[channelCount];
            _currentTimestampByChannel = new DateTimeOffset?[channelCount];
            _completedChannels = new bool[channelCount];
            _lastRateMessagesRead = 0;
            _messageRate = 0;
            _lastRateUpdate = null;
        }

        DisplayedProgressFraction = 0;
    }

    private IProgress<ExportProgress> CreateExportProgressInput(
        int index,
        IProgress<Percentage> fallbackProgress
    ) =>
        new System.Progress<ExportProgress>(progress =>
        {
            fallbackProgress.Report(progress.Fraction);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyExportProgress(index, progress));
        });

    private ExportProgressSnapshot SnapshotExportProgressUnderLock()
    {
        var messagesRead = 0L;
        var completedChannelCount = 0;
        DateTimeOffset? currentTimestamp = null;

        for (var i = 0; i < _messagesReadByChannel.Length; i++)
        {
            messagesRead += _messagesReadByChannel[i];

            if (_currentTimestampByChannel[i] is { } timestamp)
                currentTimestamp = timestamp;

            if (_completedChannels[i])
                completedChannelCount++;
        }

        return new ExportProgressSnapshot(
            messagesRead,
            currentTimestamp,
            completedChannelCount,
            _messagesReadByChannel.Length,
            _messagesReadByChannel.Length > 0
                && completedChannelCount == _messagesReadByChannel.Length
        );
    }

    private void ApplyExportProgress(int index, ExportProgress progress)
    {
        ExportProgressSnapshot snapshot;

        lock (_exportProgressLock)
        {
            if (index < 0 || index >= _messagesReadByChannel.Length)
                return;

            _messagesReadByChannel[index] = Math.Max(
                _messagesReadByChannel[index],
                progress.MessagesRead
            );
            _currentTimestampByChannel[index] = progress.CurrentTimestamp;

            snapshot = SnapshotExportProgressUnderLock();
        }

        UpdateRate(snapshot.MessagesRead, DateTimeOffset.Now);

        UpdateProgressStatusText(snapshot, snapshot.CurrentTimestamp);
    }

    private static void RunOrPostToUiThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            action();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private void MarkExportProgressCompleted(int index) =>
        RunOrPostToUiThread(() => MarkExportProgressCompletedOnUiThread(index));

    private void MarkExportProgressCompletedOnUiThread(int index)
    {
        var completedValidChannel = false;
        ExportProgressSnapshot snapshot;

        lock (_exportProgressLock)
        {
            if (index >= 0 && index < _completedChannels.Length)
            {
                completedValidChannel = true;
                _completedChannels[index] = true;
            }

            snapshot = SnapshotExportProgressUnderLock();
        }

        if (completedValidChannel && snapshot.IsRunCompleted)
            DisplayedProgressFraction = 1;

        UpdateProgressStatusText(snapshot, null);
    }

    private void UpdateRate(long messagesRead, DateTimeOffset now)
    {
        if (_lastRateUpdate is { } previousTime)
        {
            var elapsed = (now - previousTime).TotalSeconds;
            var delta = messagesRead - _lastRateMessagesRead;
            if (elapsed > 0 && delta > 0)
            {
                var instantRate = delta / elapsed;
                var alpha = 1 - Math.Exp(-elapsed / 5);
                _messageRate =
                    _messageRate <= 0
                        ? instantRate
                        : _messageRate + alpha * (instantRate - _messageRate);
            }
        }

        _lastRateUpdate = now;
        _lastRateMessagesRead = messagesRead;
    }

    private string? FormatMessagesRead(long messagesRead)
    {
        if (messagesRead <= 0)
            return null;

        var read = messagesRead.ToString("N0", CultureInfo.CurrentCulture);
        return string.Format(LocalizationManager.MessagesReadFormat, read);
    }

    private void UpdateProgressStatusText(
        ExportProgressSnapshot snapshot,
        DateTimeOffset? currentTimestamp
    )
    {
        MessagesReadText = FormatMessagesRead(snapshot.MessagesRead);

        RateText =
            _messageRate > 0
                ? string.Format(
                    LocalizationManager.MessageRateFormat,
                    _messageRate.ToString("N0", CultureInfo.CurrentCulture)
                )
                : null;

        if (currentTimestamp is not null)
        {
            ExportedThroughText = string.Format(
                LocalizationManager.ExportedThroughFormat,
                currentTimestamp.Value.ToString("MMM yyyy", CultureInfo.CurrentCulture)
            );
        }

        ChannelProgressText =
            snapshot.ChannelCount > 1
                ? string.Format(
                    LocalizationManager.ChannelProgressFormat,
                    Math.Min(snapshot.CompletedChannelCount + 1, snapshot.ChannelCount),
                    snapshot.ChannelCount
                )
                : null;
    }

    private async ValueTask CopyUserMessagesAsync(
        ExportSetupViewModel dialog,
        ChannelExporter exporter,
        CancellationToken cancellationToken
    )
    {
        var channel = dialog.Channels!.Single();
        var progress = _progressMuxer.CreateInput();
        var outputPath = Path.Combine(Path.GetTempPath(), $"{Program.Name}-{Guid.NewGuid():N}.txt");

        try
        {
            var request = new ExportRequest(
                dialog.Guild!,
                channel,
                outputPath,
                null,
                ExportFormat.PlainText,
                dialog.After?.Pipe(Snowflake.FromDate),
                dialog.Before?.Pipe(Snowflake.FromDate),
                PartitionLimit.Null,
                dialog.CopyUserMessagesFilter,
                dialog.IsReverseMessageOrder,
                dialog.ShouldFormatMarkdown,
                false,
                false,
                _settingsService.Locale,
                _settingsService.IsUtcNormalizationEnabled
            );

            StartExportProgressRun(1);
            await exporter.ExportChannelAsync(
                request,
                CreateExportProgressInput(0, progress),
                cancellationToken
            );

            var text = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var user = dialog.CopyUserMessagesUserValue?.Trim();

            if (text.Length > 0)
            {
                await SetClipboardTextAsync(text);

                _snackbarManager.Notify(
                    string.Format(LocalizationManager.SuccessfulCopyUserMessagesMessage, user)
                );
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.NoCopyUserMessagesFoundMessage, user)
                );
            }
        }
        finally
        {
            MarkExportProgressCompleted(0);
            progress.ReportCompletion();

            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary export.
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        IsBusy = true;
        ResetExportProgressDisplay();

        try
        {
            var selectedChannels = GetSelectedExportableChannels();
            if (_discord is null || SelectedGuild is null || selectedChannels.Length <= 0)
                return;

            var dialog = _viewModelManager.GetExportSetupViewModel(SelectedGuild, selectedChannels);

            if (await _dialogManager.ShowDialogAsync(dialog) != true)
                return;

            var exporter = new ChannelExporter(_discord);
            var cancellationToken = BeginCancelableOperation();

            if (dialog.ShouldCopyUserMessages)
            {
                await CopyUserMessagesAsync(dialog, exporter, cancellationToken);
                return;
            }

            var channels = dialog.Channels!.ToArray();
            var failed = await RunExportCoreAsync(exporter, dialog, channels, cancellationToken);

            _lastExportSetup = dialog;
            UpdateFailedChannels(failed);
        }
        catch (OperationCanceledException)
            when (_operationCancellation?.IsCancellationRequested == true)
        {
            _snackbarManager.Notify(LocalizationManager.CancelButton);
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            EndCancelableOperation();
            IsBusy = false;
        }
    }

    // Builds an ExportRequest for a channel using the dialog's parameters. Pure path-building.
    private ExportRequest BuildExportRequest(ExportSetupViewModel dialog, Channel channel) =>
        new(
            dialog.Guild!,
            channel,
            dialog.OutputPath!,
            dialog.AssetsDirPath,
            dialog.SelectedFormat,
            dialog.After?.Pipe(Snowflake.FromDate),
            dialog.Before?.Pipe(Snowflake.FromDate),
            dialog.PartitionLimit,
            dialog.MessageFilter,
            dialog.IsReverseMessageOrder,
            dialog.ShouldFormatMarkdown,
            dialog.ShouldDownloadAssets,
            dialog.ShouldReuseAssets,
            _settingsService.Locale,
            _settingsService.IsUtcNormalizationEnabled
        );

    private static ManifestChannelInfo BuildManifestInfo(ExportRequest r) =>
        new(
            r.Guild.Id.ToString(),
            r.Guild.Name,
            r.Channel.Id.ToString(),
            r.Channel.Name,
            r.Channel.Parent?.Name,
            r.Format.ToString()
        );

    // Best-effort per-channel manifest checkpoint. Failure must never fail the channel's export.
    // Returns true if the manifest was written, false if a write failure was swallowed (the caller
    // notifies once per run rather than once per channel).
    private async ValueTask<bool> CheckpointManifestAsync(
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
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // Adds the given output folders to the persisted KnownExportDirs (most-recent-first, deduped,
    // capped) in a single settings write, so the Library home view can discover past exports. The
    // shared seam every write path (export, retry, continue) funnels its output folder through.
    private void RegisterExportedDirs(IReadOnlyCollection<string> dirs)
    {
        if (dirs.Count == 0)
            return;

        var known = _settingsService.KnownExportDirs;
        foreach (var dir in dirs)
            known = RecentExportDirs.Add(known, dir, 200).ToArray();

        _settingsService.KnownExportDirs = known;
        _settingsService.Save();
    }

    // After a continue grows an existing export file, refresh its manifest entry (fresh count,
    // size, and hash). Best-effort: a catalog failure must never fail the continue itself.
    private async Task<bool> RefreshContinuedExportCatalogAsync(
        string filePath,
        Guild guild,
        Channel channel,
        long messageCount,
        ExportResult? appendedResult,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir))
                return false;

            var fileName = Path.GetFileName(filePath);
            var wasUpdated = false;
            await ManifestWriter.UpdateAsync(
                dir,
                existing =>
                {
                    var prior = existing?.Entries.FirstOrDefault(e =>
                        string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase)
                    );

                    var info = new ManifestChannelInfo(
                        guild.Id.ToString(),
                        guild.Name,
                        channel.Id.ToString(),
                        channel.Name,
                        channel.Parent?.Name,
                        // Preserve the original format string (e.g. HtmlLight vs HtmlDark, which
                        // the file extension alone can't distinguish); fall back to extension map.
                        prior?.Format
                            ?? ContinuationFormat.FormatFor(filePath).ToString()
                    );

                    var result = new ExportResult(
                        [new ExportedFile(filePath, messageCount, null, null, null, null)],
                        messageCount,
                        0
                    );

                    var entries = ManifestBuilder.Build(
                        info,
                        result,
                        DateTimeOffset.Now,
                        cancellationToken
                    );
                    if (entries.Count == 0)
                        return [];

                    // Continue doesn't re-scan the whole file, so carry the first-message metadata
                    // forward from the prior entry rather than nulling it; count/size/hash above
                    // are freshly read.
                    if (prior is not null)
                    {
                        var appendedFile = appendedResult?.Files.LastOrDefault(file =>
                            file.MessageCount > 0
                        );
                        var hasAppendedMessages = appendedResult?.MessageCount > 0;
                        entries =
                        [
                            entries[0] with
                            {
                                FirstMessageId = prior.FirstMessageId,
                                FirstMessageTimestamp = prior.FirstMessageTimestamp,
                                LastMessageId = hasAppendedMessages
                                    ? appendedFile?.LastMessageId?.ToString()
                                    : prior.LastMessageId,
                                LastMessageTimestamp = hasAppendedMessages
                                    ? appendedFile?.LastMessageTimestamp
                                    : prior.LastMessageTimestamp,
                                AssetCount = prior.AssetCount,
                            },
                        ];
                    }

                    wasUpdated = true;
                    return entries;
                },
                DateTimeOffset.Now,
                cancellationToken
            );

            return wasUpdated;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Best-effort: a catalog refresh failure must not fail the continue itself.
            return false;
        }
    }

    // The export core, shared by ExportAsync and the retry command. Returns the channels that failed.
    private async Task<IReadOnlyList<Channel>> RunExportCoreAsync(
        ChannelExporter exporter,
        ExportSetupViewModel dialog,
        IReadOnlyList<Channel> channels,
        CancellationToken cancellationToken
    )
    {
        var requests = channels
            .Select(c => (Channel: c, Request: BuildExportRequest(dialog, c)))
            .ToArray();

        var duplicateOutputPaths = ExportOutputPathValidator.GetDuplicateOutputFilePaths(
            requests.Select(r => r.Request)
        );
        if (duplicateOutputPaths.Count > 0)
        {
            throw new ApplicationException(
                "Multiple selected channels would be exported to the same output file. "
                    + "Choose an output folder or use a unique template token such as %c. "
                    + "Conflicting output path(s): "
                    + string.Join(", ", duplicateOutputPaths)
            );
        }

        // Resume detection: which selected channels are already exported in their target directory?
        var manifestsByDir = new Dictionary<string, ExportManifest?>(
            StringComparer.OrdinalIgnoreCase
        );
        var alreadyDone = new List<(Channel Channel, ExportRequest Request)>();

        foreach (var r in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = r.Request.OutputDirPath;
            if (!manifestsByDir.TryGetValue(dir, out var manifest))
            {
                manifest = await ManifestReader.TryReadAsync(
                    Path.Combine(dir, ExportManifest.FileName)
                );
                manifestsByDir[dir] = manifest;
            }

            if (ManifestResume.IsAlreadyExported(manifest, dir, r.Request, cancellationToken))
            {
                alreadyDone.Add(r);
            }
        }

        var toExport = requests;

        if (alreadyDone.Count > 0)
        {
            var prompt = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ResumePromptTitle,
                string.Format(LocalizationManager.ResumePromptMessage, alreadyDone.Count),
                LocalizationManager.ResumeSkipButton, // default -> true -> skip (resume)
                LocalizationManager.ResumeExportAllButton // cancel (EXPORT ALL) -> null -> export all
            );

            if (await _dialogManager.ShowDialogAsync(prompt) == true)
            {
                var doneSet = alreadyDone.Select(d => d.Channel).ToHashSet();
                toExport = requests.Where(r => !doneSet.Contains(r.Channel)).ToArray();
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ResumeSkippedMessage, alreadyDone.Count)
                );

                if (toExport.Length == 0)
                    return [];
            }
        }

        var pairs = toExport
            .Select(
                (r, index) =>
                    new
                    {
                        Index = index,
                        r.Channel,
                        r.Request,
                        Progress = _progressMuxer.CreateInput(),
                    }
            )
            .ToArray();
        StartExportProgressRun(pairs.Length);

        var exportStats = new ConcurrentBag<ChannelExportStats>();
        var failedChannels = new ConcurrentBag<Channel>();
        var exportedDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var successfulExportCount = 0;
        var catalogWriteFailed = 0;
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _settingsService.ParallelLimit),
                CancellationToken = cancellationToken,
            },
            async (pair, cancellationToken) =>
            {
                var request = pair.Request;
                var progress = pair.Progress;

                try
                {
                    var result = await exporter.ExportChannelAsync(
                        request,
                        CreateExportProgressInput(pair.Index, progress),
                        cancellationToken
                    );

                    if (!await CheckpointManifestAsync(request, result, cancellationToken))
                        Interlocked.Exchange(ref catalogWriteFailed, 1);

                    exportStats.Add(
                        new ChannelExportStats(
                            result.MessageCount,
                            result.AssetCount,
                            SumFileSizes(result.Files)
                        )
                    );

                    Interlocked.Increment(ref successfulExportCount);

                    // Remember this export's output folder so the Library can catalog it later.
                    exportedDirs.TryAdd(request.OutputDirPath, 0);
                }
                catch (ChannelEmptyException ex)
                {
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));

                    // Empty channels still produce an (empty) file via exporter disposal; checkpoint it
                    // so it counts as "done" for resume, consistent with the filtered-to-empty case.
                    if (
                        !await CheckpointManifestAsync(
                            request,
                            new ExportResult(
                                [
                                    new ExportedFile(
                                        request.OutputFilePath,
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
                            cancellationToken
                        )
                    )
                        Interlocked.Exchange(ref catalogWriteFailed, 1);

                    // Track the folder even for an empty export so the Library can catalog it.
                    exportedDirs.TryAdd(request.OutputDirPath, 0);
                }
                catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                {
                    failedChannels.Add(pair.Channel);
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked/full-disk output file (e.g. from the writer's final flush in
                    // DisposeAsync) must fail only this channel, not abort the whole parallel batch.
                    failedChannels.Add(pair.Channel);
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                }
                finally
                {
                    MarkExportProgressCompleted(pair.Index);
                    progress.ReportCompletion();
                }
            }
        );

        stopwatch.Stop();

        if (successfulExportCount > 0)
        {
            var summary = ExportSummarizer.Summarize(
                exportStats.ToArray(),
                failedChannels.Count,
                stopwatch.Elapsed
            );

            _snackbarManager.Notify(FormatExportSummary(summary));
        }

        // Persist every folder we wrote into (including empty-channel exports) so the Library home
        // view can discover them — even when no channel had messages.
        RegisterExportedDirs(exportedDirs.Keys.ToArray());

        // Best-effort: a manifest write failure never fails the export, but surface it once per run
        // rather than once per affected channel.
        if (catalogWriteFailed == 1)
            _snackbarManager.Notify(
                LocalizationManager.ExportCatalogWriteFailedMessage.TrimEnd('.')
            );

        CompletionAttention.FlashIfUnfocused();

        return failedChannels.ToArray();
    }

    // Records which channels failed the last run and refreshes the retry command's state. Shared by
    // the export and retry paths so this bookkeeping can't drift between them.
    private void UpdateFailedChannels(IReadOnlyList<Channel> failed)
    {
        _lastFailedChannels = failed;
        OnPropertyChanged(nameof(HasFailedExport));
        RetryFailedExportCommand.NotifyCanExecuteChanged();
    }

    private void ClearFailedExportState()
    {
        _lastExportSetup = null;
        UpdateFailedChannels([]);
    }

    private bool CanRetryFailedExport() =>
        !IsBusy
        && _discord is not null
        && _lastExportSetup is not null
        && _lastFailedChannels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailedExport))]
    private async Task RetryFailedExportAsync()
    {
        if (_discord is null || _lastExportSetup is null || _lastFailedChannels.Count == 0)
            return;

        IsBusy = true;
        ResetExportProgressDisplay();

        try
        {
            var exporter = new ChannelExporter(_discord);
            var cancellationToken = BeginCancelableOperation();
            var failed = await RunExportCoreAsync(
                exporter,
                _lastExportSetup,
                _lastFailedChannels,
                cancellationToken
            );
            UpdateFailedChannels(failed);
        }
        catch (OperationCanceledException)
            when (_operationCancellation?.IsCancellationRequested == true)
        {
            _snackbarManager.Notify(LocalizationManager.CancelButton);
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            EndCancelableOperation();
            IsBusy = false;
        }
    }

    public bool HasFailedExport => _lastFailedChannels.Count > 0;

    // Must be gated like Export (guild + channels selected), otherwise the Continue FAB floats into
    // the Export FAB's slot whenever you're merely authenticated. Regressed twice now — see the
    // DashboardCommandGatingTests guard.
    private bool CanContinueExport() =>
        !IsBusy
        && _discord is not null
        && SelectedGuild is not null
        && HasSelectedExportableChannels();

    [RelayCommand(CanExecute = nameof(CanContinueExport))]
    private async Task ContinueExportAsync()
    {
        var selectedChannels = GetSelectedExportableChannels();
        if (_discord is null || SelectedGuild is null || selectedChannels.Length <= 0)
            return;

        var selectedGuild = SelectedGuild;
        var selectedChannelsById = selectedChannels.ToDictionary(c => c.Id);

        IsBusy = true;
        ResetExportProgressDisplay();

        try
        {
            var cancellationToken = BeginCancelableOperation();
            var selectedChannelIds = selectedChannels.Select(c => c.Id).ToArray();
            var discovery = await ContinueExportDiscovery.ResolveAsync(
                _settingsService.KnownExportDirs,
                selectedChannelIds
            );
            cancellationToken.ThrowIfCancellationRequested();

            var unresolved = discovery.Unresolved.ToList();
            IReadOnlyList<ResolvedContinueTarget> targets;
            if (ShouldPromptAnchorPicker(discovery))
            {
                var pickedTarget = await ResolvePickedContinueTargetAsync(
                    selectedGuild,
                    selectedChannelsById,
                    cancellationToken
                );
                if (pickedTarget is null)
                    return;

                targets = [pickedTarget];
                unresolved.Clear();
            }
            else
            {
                targets = await HydrateDiscoveredContinueTargetsAsync(
                    discovery.Resolved,
                    selectedGuild,
                    selectedChannelsById,
                    unresolved,
                    cancellationToken
                );
            }

            if (targets.Count == 0)
            {
                var skippedOnlyMessage = FormatContinueSummary(
                    new ContinueExportRunSummary(0, 0, false, []),
                    unresolved.Count
                );
                if (!string.IsNullOrWhiteSpace(skippedOnlyMessage))
                    _snackbarManager.Notify(skippedOnlyMessage.TrimEnd('.'));

                return;
            }

            var exporter = new ChannelExporter(_discord);
            var pairs = targets
                .Select(
                    (target, index) =>
                        new
                        {
                            Index = index,
                            Target = target,
                            Progress = _progressMuxer.CreateInput(),
                        }
                )
                .ToArray();
            var pairsByTarget = pairs.ToDictionary(p => p.Target);

            StartExportProgressRun(pairs.Length);

            var summary = await RunContinueLoopAsync(
                pairs.Select(p => p.Target).ToArray(),
                (target, cancellationToken) =>
                {
                    var pair = pairsByTarget[target];
                    return ContinueExportFileAsync(
                        exporter,
                        target,
                        pair.Index,
                        pair.Progress,
                        cancellationToken
                    );
                },
                cancellationToken
            );

            var message = FormatContinueSummary(summary, unresolved.Count);
            if (!string.IsNullOrWhiteSpace(message))
                _snackbarManager.Notify(message.TrimEnd('.'));

            CompletionAttention.FlashIfUnfocused();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (OperationCanceledException)
            when (_operationCancellation?.IsCancellationRequested == true)
        {
            _snackbarManager.Notify(LocalizationManager.CancelButton);
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );
            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            EndCancelableOperation();
            IsBusy = false;
        }
    }

    private static bool ShouldPromptAnchorPicker(ContinueDiscoveryResult result) =>
        result.Resolved.Count == 0;

    internal async Task<
        IReadOnlyList<ResolvedContinueTarget>
    > HydrateDiscoveredContinueTargetsAsync(
        IReadOnlyList<ResolvedCatalogEntry> entries,
        Guild selectedGuild,
        IReadOnlyDictionary<Snowflake, Channel> selectedChannelsById,
        ICollection<UnresolvedCatalogChannel> unresolved,
        CancellationToken cancellationToken = default
    )
    {
        var targets = new List<ResolvedContinueTarget>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The cutoff read happens up front, outside the per-channel export loop's isolation,
            // so a single empty or unreadable existing export (e.g. a JSON with no messages) must
            // not be allowed to abort the whole batch — skip that channel and continue.
            ContinuationCutoff cutoff;
            try
            {
                cutoff = await RunContinuationWorkOffUiThreadAsync(
                    () => ContinuationFormat.ReadCutoffAsync(entry.FilePath, cancellationToken),
                    cancellationToken
                );
            }
            catch (DiscordChatExporterException ex) when (!ex.IsFatal)
            {
                unresolved.Add(
                    new UnresolvedCatalogChannel(
                        entry.ChannelId,
                        ContinueSkipReason.CutoffUnreadable
                    )
                );
                continue;
            }

            if (!cutoff.IsChronological)
            {
                unresolved.Add(
                    new UnresolvedCatalogChannel(
                        entry.ChannelId,
                        ContinueSkipReason.ReverseChronological
                    )
                );
                continue;
            }

            if (!selectedChannelsById.TryGetValue(entry.ChannelId, out var channel))
                continue;

            targets.Add(
                new ResolvedContinueTarget(
                    channel,
                    channel.IsDirect ? Guild.DirectMessages : selectedGuild,
                    entry.FilePath,
                    Path.GetDirectoryName(entry.FilePath) ?? string.Empty,
                    entry.Format,
                    cutoff
                )
            );
        }

        return targets;
    }

    private async Task<ResolvedContinueTarget?> ResolvePickedContinueTargetAsync(
        Guild selectedGuild,
        IReadOnlyDictionary<Snowflake, Channel> selectedChannelsById,
        CancellationToken cancellationToken = default
    )
    {
        var filePath = await _dialogManager.PromptSingleFilePathAsync(
            CreateContinueExportFileTypes()
        );
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        if (!ContinuationFormat.IsSupportedExtension(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportFormatUnsupportedMessage.TrimEnd('.')
            );
            return null;
        }

        if (IsPartitionedExportPath(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportPartitionedUnsupportedMessage.TrimEnd('.')
            );
            return null;
        }

        ContinuationCutoff cutoff;
        try
        {
            cutoff = await RunContinuationWorkOffUiThreadAsync(
                () => ContinuationFormat.ReadCutoffAsync(filePath, cancellationToken),
                cancellationToken
            );
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            // The inspector already produced a user-friendly reason (corrupt/empty/unsupported file).
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
            return null;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // A malformed picked file can still throw a raw parse/IO error; show a friendly message
            // instead of letting it bubble to the generic stack-trace dialog.
            _snackbarManager.Notify(
                $"Could not read '{Path.GetFileName(filePath)}'. It may be corrupted or unsupported."
            );
            return null;
        }

        if (!cutoff.IsChronological)
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportReverseUnsupportedMessage.TrimEnd('.')
            );
            return null;
        }

        var channel = selectedChannelsById.TryGetValue(cutoff.ChannelId, out var selectedChannel)
            ? selectedChannel
            : await _discord!.GetChannelAsync(cutoff.ChannelId, cancellationToken);

        return new ResolvedContinueTarget(
            channel,
            channel.IsDirect ? Guild.DirectMessages : selectedGuild,
            filePath,
            Path.GetDirectoryName(filePath) ?? string.Empty,
            ContinuationFormat.FormatFor(filePath),
            cutoff
        );
    }

    private async Task<ContinueExportFileResult> ContinueExportFileAsync(
        ChannelExporter exporter,
        ResolvedContinueTarget target,
        int progressIndex,
        ICompletableProgress<Percentage> progress,
        CancellationToken cancellationToken
    )
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"{Program.Name}-continue-{Guid.NewGuid():N}{Path.GetExtension(target.FilePath)}"
        );

        try
        {
            var request = BuildContinueExportRequest(target, tempPath);
            ExportResult? appendedResult = null;

            try
            {
                appendedResult = await exporter.ExportChannelAsync(
                    request,
                    CreateExportProgressInput(progressIndex, progress),
                    cancellationToken
                );
            }
            catch (ChannelEmptyException)
            {
                if (target.Format is not ExportFormat.Db)
                    return new ContinueExportFileResult(true, 0, true);
            }

            var countBefore = target.Cutoff.ExistingCount;
            var total = await RunContinuationWorkOffUiThreadAsync(
                () =>
                    ContinuationFormat.MergeAsync(
                        target.FilePath,
                        tempPath,
                        target.Cutoff,
                        DateTimeOffset.Now,
                        cancellationToken
                    ),
                cancellationToken
            );
            var newMessages = total - countBefore;
            var isCatalogRefreshed = await RefreshContinuedExportCatalogAsync(
                target.FilePath,
                target.Guild,
                target.Channel,
                total,
                appendedResult,
                cancellationToken
            );

            return new ContinueExportFileResult(true, newMessages, isCatalogRefreshed);
        }
        finally
        {
            MarkExportProgressCompleted(progressIndex);
            progress.ReportCompletion();
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary export.
            }
        }
    }

    private ExportRequest BuildContinueExportRequest(
        ResolvedContinueTarget target,
        string outputPath
    ) =>
        new(
            target.Guild,
            target.Channel,
            outputPath,
            null,
            target.Format,
            target.Cutoff.Cutoff,
            target.Cutoff.Before,
            PartitionLimit.Null,
            MessageFilter.Null,
            false,
            _settingsService.LastShouldFormatMarkdown,
            false,
            false,
            _settingsService.Locale,
            _settingsService.IsUtcNormalizationEnabled
        );

    internal async Task<ContinueExportRunSummary> RunContinueLoopAsync(
        IReadOnlyList<ResolvedContinueTarget> targets,
        Func<ResolvedContinueTarget, CancellationToken, Task<ContinueExportFileResult>> processOne,
        CancellationToken cancellationToken
    )
    {
        var processed = new List<ContinueExportFileResult>();
        var failedChannels = new List<Channel>();
        var exportedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogWriteFailed = false;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await processOne(target, cancellationToken);
                if (!result.WasProcessed)
                    continue;

                processed.Add(result);
                if (!result.IsCatalogRefreshed)
                    catalogWriteFailed = true;

                exportedDirs.Add(target.Dir);
            }
            catch (DiscordChatExporterException ex) when (!ex.IsFatal)
            {
                failedChannels.Add(target.Channel);
                _snackbarManager.Notify(ex.Message.TrimEnd('.'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Belt-and-suspenders: the mergers now raise non-fatal InvalidExportException, but
                // isolate any stray IO failure from another continue step to this channel.
                failedChannels.Add(target.Channel);
                _snackbarManager.Notify(ex.Message.TrimEnd('.'));
            }
        }

        RegisterExportedDirs(exportedDirs.ToArray());
        return new ContinueExportRunSummary(
            processed.Count,
            processed.Sum(p => p.NewMessages),
            catalogWriteFailed,
            failedChannels
        );
    }

    internal string FormatContinueSummary(ContinueExportRunSummary summary, int skippedCount)
    {
        var parts = new List<string>();

        if (summary.ProcessedCount > 0)
        {
            parts.Add(
                summary.TotalNewMessages <= 0
                    ? LocalizationManager.ContinueExportUpToDateMessage
                    : string.Format(
                        LocalizationManager.ContinueExportSuccessMessage,
                        summary.TotalNewMessages
                    )
            );
        }

        if (skippedCount > 0)
            parts.Add(string.Format(LocalizationManager.ContinueExportSkippedTail, skippedCount));

        if (summary.FailedChannels.Count > 0)
            parts.Add(
                string.Format(
                    LocalizationManager.ContinueExportFailedTail,
                    summary.FailedChannels.Count
                )
            );

        if (summary.CatalogWriteFailed)
            parts.Add(LocalizationManager.ExportCatalogWriteFailedMessage);

        return string.Join(" ", parts);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:D2}s"
        : $"{t.Seconds}s";

    private string FormatExportSummary(ExportSummary summary)
    {
        var message = string.Format(
            LocalizationManager.ExportSummaryMessage,
            summary.SucceededChannels,
            summary.TotalMessages.ToString("N0", CultureInfo.CurrentCulture),
            summary.TotalAssets.ToString("N0", CultureInfo.CurrentCulture),
            FileSize.FromBytes(summary.TotalBytes).ToString(),
            FormatDuration(summary.Duration)
        );

        if (summary.FailedChannels > 0)
            message += string.Format(
                LocalizationManager.ExportSummaryFailedSuffix,
                summary.FailedChannels
            );

        return message;
    }

    private static long SumFileSizes(IReadOnlyList<ExportedFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                total += new FileInfo(file.FilePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a size we can't read just doesn't contribute to the total.
            }
        }

        return total;
    }

    private static bool IsPartitionedExportPath(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Contains(" [part ", StringComparison.OrdinalIgnoreCase))
            return true;

        var dir = Path.GetDirectoryName(filePath);
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(dir))
            return false;

        var sibling = Path.Combine(dir, $"{fileName} [part 2]{ext}");
        return File.Exists(sibling);
    }

    private static IReadOnlyList<FilePickerFileType> CreateContinueExportFileTypes() =>
        [
            new FilePickerFileType("Supported exports (JSON, HTML, CSV, SQLite)")
            {
                Patterns = ["*.json", "*.html", "*.htm", "*.csv", "*.db"],
            },
        ];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();

            if (_discord is not null)
            {
                _discord.RateLimitChanged -= HandleRateLimitChanged;
                _discord.Dispose();
            }

            _eventSubscription.Dispose();
        }

        base.Dispose(disposing);
    }
}
