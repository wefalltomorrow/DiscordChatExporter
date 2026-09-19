using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

// Guards the Continue-export FAB gating, which has regressed TWICE: CanContinueExport must require a
// selected guild + channels exactly like CanExport. If it only checks authentication, the Continue
// FAB floats into the Export FAB's slot whenever the user is merely logged in (no channel chosen).
public sealed class DashboardCommandGatingTests
{
    private static DashboardViewModel CreateViewModel()
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

        var provider = services.BuildServiceProvider(true);
        return provider.GetRequiredService<DashboardViewModel>();
    }

    // _discord is private and only set after a real token auth; fake it so the gate's *other*
    // conditions (guild + channel selection) are what's under test.
    private static void Authenticate(DashboardViewModel vm)
    {
        typeof(DashboardViewModel)
            .GetField("_discord", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new DiscordClient("fake-token"));
        vm.SelectedGuild = new Guild(new Snowflake(1), "Test Guild", "");
    }

    private static Channel CreateChannel(
        ulong id,
        ChannelKind kind = ChannelKind.GuildTextChat,
        Channel? parent = null
    ) =>
        new(
            new Snowflake(id),
            kind,
            new Snowflake(1),
            parent,
            $"channel-{id}",
            0,
            null,
            null,
            false,
            null
        );

    [AvaloniaFact]
    public void Continue_export_is_gated_like_export_when_no_channel_is_selected()
    {
        var vm = CreateViewModel();
        Authenticate(vm);

        // Authenticated with a guild, but NO channels selected — this is the state where the bug
        // shows the Continue FAB floating in the Export FAB's place.
        vm.SelectedChannels.Should().BeEmpty();

        vm.ContinueExportCommand.CanExecute(null)
            .Should()
            .BeFalse("Continue-export must require a selected channel, exactly like Export");
        vm.ExportCommand.CanExecute(null)
            .Should()
            .BeFalse("Export is also disabled with no channel — Continue must match it");
    }

    [AvaloniaFact]
    public void Export_commands_are_disabled_when_only_a_category_is_selected()
    {
        var vm = CreateViewModel();
        Authenticate(vm);
        vm.SelectedChannels.Add(
            new ChannelConnection(CreateChannel(10, ChannelKind.GuildCategory), [])
        );

        vm.ExportCommand.CanExecute(null).Should().BeFalse();
        vm.ContinueExportCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public void Cancel_operation_command_cancels_the_active_dashboard_token()
    {
        var vm = CreateViewModel();
        var token = vm.BeginCancelableOperation();

        vm.CancelOperationCommand.CanExecute(null).Should().BeTrue();
        vm.CancelOperationCommand.Execute(null);

        token.IsCancellationRequested.Should().BeTrue();
        vm.CancelOperationCommand.CanExecute(null).Should().BeFalse();
        vm.EndCancelableOperation();
    }

    [AvaloniaFact]
    public void Select_all_toggles_only_exportable_channels()
    {
        var vm = CreateViewModel();
        var category = CreateChannel(10, ChannelKind.GuildCategory);
        var categoryChild = CreateChannel(11, parent: category);
        var parentChannel = CreateChannel(12);
        var thread = CreateChannel(13, ChannelKind.GuildPublicThread, parentChannel);
        vm.AvailableChannels =
        [
            new ChannelConnection(category, [new ChannelConnection(categoryChild, [])]),
            new ChannelConnection(parentChannel, [new ChannelConnection(thread, [])]),
        ];

        vm.SelectAllChannelsCommand.CanExecute(null).Should().BeTrue();
        vm.SelectAllChannelsCommand.Execute(null);

        vm.SelectedChannels.Select(c => c.Channel.Id.Value).Should().Equal(11, 12, 13);
        vm.AllChannelsSelected.Should().BeTrue();
        vm.SelectAllChannelsButtonText.Should().Contain("Deselect");

        vm.SelectAllChannelsCommand.Execute(null);

        vm.SelectedChannels.Should().BeEmpty();
        vm.AllChannelsSelected.Should().BeFalse();
    }
}
