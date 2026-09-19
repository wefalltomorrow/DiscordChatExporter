using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.ViewModels.Components;
using PowerKit.Extensions;

namespace DiscordChatExporter.Gui.Views.Components;

public partial class DashboardView : UserControl<DashboardViewModel>
{
    public DashboardView() => InitializeComponent();

    private void UserControl_OnLoaded(object? sender, RoutedEventArgs args) =>
        TokenValueTextBox.Focus();

    private void AvailableGuildsListBox_OnSelectionChanged(
        object? sender,
        SelectionChangedEventArgs args
    ) => DataContext.PullChannelsCommand.ExecuteIfCan(null);

    private void AvailableChannelsTreeView_OnSelectionChanged(
        object? sender,
        SelectionChangedEventArgs args
    )
    {
        // Categories can't be exported, so unselect any that just got selected.
        var categories = args
            .AddedItems.OfType<ChannelConnection>()
            .Where(x => x.Channel.IsCategory)
            .ToArray();

        if (categories.Length == 0)
            return;

        // Defer to the next dispatcher cycle: setting IsSelected here would mutate the TreeView's
        // SelectedItems collection while it is still raising the CollectionChanged event that drove
        // this SelectionChanged, throwing "Cannot change ObservableCollection during a
        // CollectionChanged event" (Avalonia 12 raises it synchronously inside the mutation).
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in categories)
            {
                if (AvailableChannelsTreeView.TreeContainerFromItem(item) is TreeViewItem container)
                    container.IsSelected = false;
            }
        });
    }

    private void ChannelGrid_OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext.SelectedChannels.Count != 1)
            return;

        if (DataContext.SelectedChannels[0].Channel.IsCategory)
            return;

        DataContext.ExportCommand.ExecuteIfCan(null);
    }
}
