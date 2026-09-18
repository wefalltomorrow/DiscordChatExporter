using Avalonia.Headless.XUnit;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

// Proves the load-bearing wiring behind the Dashboard <-> Library view switch (Task 4):
// MainViewModel.CurrentPage starts on the held Dashboard instance, becomes a fresh
// LibraryViewModel when the Dashboard raises LibraryRequested (via NavigateToLibraryCommand),
// and returns to the SAME Dashboard instance when the library raises BackRequested
// (via NavigateBackCommand).
public sealed class MainViewNavigationTests
{
    // Minimal DI graph mirroring App.axaml.cs — enough to resolve a real MainViewModel and let
    // ViewModelManager hand out real Dashboard/Library instances from the provider.
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Framework
        services.AddSingleton<DialogManager>();
        services.AddSingleton<SnackbarManager>();
        services.AddSingleton<ViewManager>();
        services.AddSingleton<ViewModelManager>();

        // Services
        services.AddSingleton(TestSettingsServiceFactory.Create());
        services.AddSingleton<UpdateService>();

        // Localization
        services.AddSingleton<LocalizationManager>();

        // View models
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<ConversionViewModel>();
        services.AddTransient<ExportSetupViewModel>();
        services.AddTransient<MessageBoxViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(true);
    }

    [AvaloniaFact]
    public void CurrentPage_switches_to_a_fresh_Library_and_back_to_the_same_Dashboard()
    {
        using var provider = BuildServices();

        // Avoid the InitializeAsync side-effects (Ukraine dialog needs a DialogHost).
        var settings = provider.GetRequiredService<SettingsService>();
        settings.IsUkraineSupportMessageEnabled = false;

        // Navigation (CurrentPage = Dashboard + the LibraryRequested subscription) is wired in the
        // constructor, so it's live immediately — InitializeAsync isn't needed for it.
        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        // Initial page is the Dashboard.
        var dashboard = mainViewModel.Dashboard;
        mainViewModel.CurrentPage.Should().BeSameAs(dashboard);

        // Dashboard -> Library: a fresh LibraryViewModel becomes the current page.
        dashboard.NavigateToLibraryCommand.Execute(null);
        mainViewModel.CurrentPage.Should().BeOfType<LibraryViewModel>();

        var library = (LibraryViewModel)mainViewModel.CurrentPage!;

        // Library -> Dashboard: back returns to the SAME held Dashboard instance.
        library.NavigateBackCommand.Execute(null);
        mainViewModel.CurrentPage.Should().BeSameAs(dashboard);
    }

    [AvaloniaFact]
    public void Each_navigation_creates_a_distinct_Library_instance()
    {
        using var provider = BuildServices();

        var settings = provider.GetRequiredService<SettingsService>();
        settings.IsUkraineSupportMessageEnabled = false;

        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        var dashboard = mainViewModel.Dashboard;

        dashboard.NavigateToLibraryCommand.Execute(null);
        var firstLibrary = mainViewModel.CurrentPage;

        ((LibraryViewModel)firstLibrary!).NavigateBackCommand.Execute(null);

        dashboard.NavigateToLibraryCommand.Execute(null);
        var secondLibrary = mainViewModel.CurrentPage;

        secondLibrary.Should().NotBeSameAs(firstLibrary);
    }

    [AvaloniaFact]
    public void CurrentPage_switches_to_Conversion_and_back_to_Dashboard()
    {
        using var provider = BuildServices();

        var settings = provider.GetRequiredService<SettingsService>();
        settings.IsUkraineSupportMessageEnabled = false;

        var mainViewModel = provider.GetRequiredService<MainViewModel>();
        var dashboard = mainViewModel.Dashboard;

        dashboard.NavigateToConversionCommand.Execute(null);
        mainViewModel.CurrentPage.Should().BeOfType<ConversionViewModel>();

        ((ConversionViewModel)mainViewModel.CurrentPage!).NavigateBackCommand.Execute(null);
        mainViewModel.CurrentPage.Should().BeSameAs(dashboard);
    }

    [AvaloniaFact]
    public void Dashboard_navigation_commands_are_disabled_while_busy()
    {
        using var provider = BuildServices();

        var settings = provider.GetRequiredService<SettingsService>();
        settings.IsUkraineSupportMessageEnabled = false;

        var mainViewModel = provider.GetRequiredService<MainViewModel>();
        var dashboard = mainViewModel.Dashboard;

        dashboard.IsBusy = true;

        dashboard.NavigateToLibraryCommand.CanExecute(null).Should().BeFalse();
        dashboard.NavigateToConversionCommand.CanExecute(null).Should().BeFalse();
    }
}
