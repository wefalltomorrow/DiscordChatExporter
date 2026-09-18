using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Onova;
using Onova.Exceptions;
using Onova.Services;

namespace DiscordChatExporter.Gui.Services;

public class UpdateService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly IUpdateManager? _updateManager;

    private Version? _updateVersion;
    private bool _isUpdatePrepared;
    private bool _isUpdaterLaunched;

    public UpdateService(SettingsService settingsService)
        : this(settingsService, CreateDefaultUpdateManager()) { }

    internal UpdateService(SettingsService settingsService, IUpdateManager? updateManager)
    {
        _settingsService = settingsService;
        _updateManager = updateManager;
    }

    private void ClearPreparedUpdate()
    {
        _updateVersion = null;
        _isUpdatePrepared = false;
    }

    private static IUpdateManager? CreateDefaultUpdateManager() =>
        OperatingSystem.IsWindows() && StartOptions.Current.IsAutoUpdateAllowed
            ? new UpdateManager(
                new GithubPackageResolver(
                    "arandomhooman",
                    "DiscordChatExporter",
                    // Examples:
                    // DiscordChatExporter.win-arm64.zip
                    // DiscordChatExporter.win-x64.zip
                    // DiscordChatExporter.linux-x64.zip
                    $"DiscordChatExporter.{RuntimeInformation.RuntimeIdentifier}.zip"
                ),
                new ZipPackageExtractor()
            )
            : null;

    public async ValueTask<Version?> CheckForUpdatesAsync()
    {
        if (_updateManager is null)
            return null;

        if (!_settingsService.IsAutoUpdateEnabled)
            return null;

        var check = await _updateManager.CheckForUpdatesAsync();
        return check.CanUpdate ? check.LastVersion : null;
    }

    public async ValueTask<bool> PrepareUpdateAsync(Version version)
    {
        ClearPreparedUpdate();

        if (_updateManager is null)
            return false;

        if (!_settingsService.IsAutoUpdateEnabled)
            return false;

        try
        {
            await _updateManager.PrepareUpdateAsync(version);
            _updateVersion = version;
            _isUpdatePrepared = true;
            return true;
        }
        catch (UpdaterAlreadyLaunchedException)
        {
            // Ignore race conditions
            return false;
        }
        catch (LockFileNotAcquiredException)
        {
            // Ignore race conditions
            return false;
        }
    }

    public bool FinalizeUpdate(bool needRestart)
    {
        if (_updateManager is null)
            return false;

        if (!_settingsService.IsAutoUpdateEnabled)
            return false;

        if (_updateVersion is null || !_isUpdatePrepared || _isUpdaterLaunched)
            return false;

        try
        {
            _updateManager.LaunchUpdater(_updateVersion, needRestart);
            _isUpdaterLaunched = true;
            return true;
        }
        catch (UpdaterAlreadyLaunchedException)
        {
            // Ignore race conditions
            return false;
        }
        catch (LockFileNotAcquiredException)
        {
            // Ignore race conditions
            return false;
        }
    }

    public void Dispose() => _updateManager?.Dispose();
}
