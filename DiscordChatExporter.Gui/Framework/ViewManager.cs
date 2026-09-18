using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using DiscordChatExporter.Gui.Views;
using DiscordChatExporter.Gui.Views.Components;
using DiscordChatExporter.Gui.Views.Dialogs;

namespace DiscordChatExporter.Gui.Framework;

public partial class ViewManager
{
    private readonly object _initializationLock = new();
    private readonly ConditionalWeakTable<ViewModelBase, object> _initializedViewModels = new();

    private static readonly object InitializedViewModelMarker = new();

    private Control? TryCreateView(ViewModelBase viewModel) =>
        viewModel switch
        {
            MainViewModel => new MainView(),
            DashboardViewModel => new DashboardView(),
            LibraryViewModel => new LibraryView(),
            ConversionViewModel => new ConversionView(),
            ExportSetupViewModel => new ExportSetupView(),
            MessageBoxViewModel => new MessageBoxView(),
            SettingsViewModel => new SettingsView(),
            _ => null,
        };

    internal async Task InitializeViewModelOnceAsync(ViewModelBase viewModel)
    {
        lock (_initializationLock)
        {
            if (_initializedViewModels.TryGetValue(viewModel, out _))
                return;

            _initializedViewModels.Add(viewModel, InitializedViewModelMarker);
        }

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public Control? TryBindView(ViewModelBase viewModel)
    {
        var view = TryCreateView(viewModel);
        if (view is null)
            return null;

        view.DataContext ??= viewModel;
        view.Loaded += async (_, _) => await InitializeViewModelOnceAsync(viewModel);

        return view;
    }

    public UserControl<T>? TryBindUserControl<T>(T viewModel)
        where T : ViewModelBase => TryBindView(viewModel) as UserControl<T>;

    public Window<T>? TryBindWindow<T>(T viewModel)
        where T : ViewModelBase => TryBindView(viewModel) as Window<T>;
}

public partial class ViewManager : IDataTemplate
{
    bool IDataTemplate.Match(object? data) => data is ViewModelBase;

    Control? ITemplate<object?, Control?>.Build(object? data) =>
        data is ViewModelBase viewModel ? TryBindView(viewModel) : null;
}
