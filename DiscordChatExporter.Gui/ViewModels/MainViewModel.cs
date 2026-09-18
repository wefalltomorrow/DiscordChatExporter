using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using PowerKit.Extensions;

namespace DiscordChatExporter.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ViewModelManager viewModelManager;
    private readonly DialogManager dialogManager;
    private readonly SnackbarManager snackbarManager;
    private readonly SettingsService settingsService;
    private readonly UpdateService updateService;
    private readonly LocalizationManager localizationManager;

    public MainViewModel(
        ViewModelManager viewModelManager,
        DialogManager dialogManager,
        SnackbarManager snackbarManager,
        SettingsService settingsService,
        UpdateService updateService,
        LocalizationManager localizationManager
    )
    {
        this.viewModelManager = viewModelManager;
        this.dialogManager = dialogManager;
        this.snackbarManager = snackbarManager;
        this.settingsService = settingsService;
        this.updateService = updateService;
        this.localizationManager = localizationManager;

        Dashboard = viewModelManager.GetDashboardViewModel();

        // Show the Dashboard by default and wire the one-time Dashboard -> Library navigation at
        // construction, so the main content is populated before the first render instead of being
        // deferred to InitializeAsync (which only runs on the view's Loaded event).
        CurrentPage = Dashboard;
        Dashboard.LibraryRequested += (_, _) => ShowLibrary();
        Dashboard.ConversionRequested += (_, _) => ShowConversion();
    }

    public string Title { get; } = $"{Program.Name} v{Program.VersionString}";

    public DashboardViewModel Dashboard { get; }

    // The view currently shown in the main content area: either the Dashboard or the Library.
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    // Switches the main content to a fresh Library instance so its catalog reloads each time it is
    // opened. The back affordance returns to the same (held) Dashboard instance.
    private void ShowLibrary()
    {
        var library = viewModelManager.GetLibraryViewModel();
        library.BackRequested += (_, _) => CurrentPage = Dashboard;
        CurrentPage = library;
    }

    private void ShowConversion()
    {
        var conversion = viewModelManager.GetConversionViewModel();
        conversion.BackRequested += (_, _) => CurrentPage = Dashboard;
        CurrentPage = conversion;
    }

    private async Task ShowUkraineSupportMessageAsync()
    {
        if (!settingsService.IsUkraineSupportMessageEnabled)
            return;

        var dialog = viewModelManager.GetMessageBoxViewModel(
            localizationManager.UkraineSupportTitle,
            localizationManager.UkraineSupportMessage,
            localizationManager.LearnMoreButton,
            localizationManager.CloseButton
        );

        // Disable this message in the future
        settingsService.IsUkraineSupportMessageEnabled = false;
        settingsService.Save();

        if (await dialogManager.ShowDialogAsync(dialog) == true)
            Process.StartShellExecute("https://tyrrrz.me/ukraine?source=discordchatexporter");
    }

    private async Task ShowDevelopmentBuildMessageAsync()
    {
        if (!Program.IsDevelopmentBuild)
            return;

        // If debugging, the user is likely a developer
        if (Debugger.IsAttached)
            return;

        var dialog = viewModelManager.GetMessageBoxViewModel(
            localizationManager.UnstableBuildTitle,
            string.Format(localizationManager.UnstableBuildMessage, Program.Name),
            localizationManager.SeeReleasesButton,
            localizationManager.CloseButton
        );

        if (await dialogManager.ShowDialogAsync(dialog) == true)
            Process.StartShellExecute(Program.ProjectReleasesUrl);
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var updateVersion = await updateService.CheckForUpdatesAsync();
            if (updateVersion is null)
                return;

            snackbarManager.Notify(
                string.Format(
                    localizationManager.UpdateDownloadingMessage,
                    Program.Name,
                    updateVersion
                )
            );
            if (!await updateService.PrepareUpdateAsync(updateVersion))
            {
                snackbarManager.Notify(localizationManager.UpdateFailedMessage);
                return;
            }

            snackbarManager.Notify(
                localizationManager.UpdateReadyMessage,
                localizationManager.UpdateInstallNowButton,
                () =>
                {
                    if (updateService.FinalizeUpdate(true))
                        App.Shutdown(2);
                    else
                        snackbarManager.Notify(localizationManager.UpdateFailedMessage);
                }
            );
        }
        catch
        {
            // Failure to update shouldn't crash the application
            snackbarManager.Notify(localizationManager.UpdateFailedMessage);
        }
    }

    public override async Task InitializeAsync()
    {
        // Navigation is wired in the constructor; only the async startup prompts remain here.
        await ShowUkraineSupportMessageAsync();
        await ShowDevelopmentBuildMessageAsync();
        await CheckForUpdatesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Save settings
            settingsService.Save();

            // Finalize pending updates
            updateService.FinalizeUpdate(false);
        }

        base.Dispose(disposing);
    }
}
