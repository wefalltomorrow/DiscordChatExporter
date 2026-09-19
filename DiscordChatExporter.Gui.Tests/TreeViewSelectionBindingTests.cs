using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DiscordChatExporter.Gui.Tests;
using FluentAssertions;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DiscordChatExporter.Gui.Tests;

// Minimal headless Avalonia application with a theme so TreeView/TreeViewItem
// have control templates (and therefore realize containers) during the test.
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

// Proves the load-bearing assumption behind the "Select all" feature:
// the dashboard binds TreeView.SelectedItems to the SelectedChannels collection
// (SelectionMode=Multiple) and the command populates that collection from code.
// This verifies the Avalonia 12 binding actually adopts and reflects programmatic
// additions instead of overwriting them — i.e. that Export will see the channels.
public sealed class TreeViewSelectionBindingTests
{
    private sealed class Node(string name, bool isCategory, params Node[] children)
    {
        public string Name { get; } = name;
        public bool IsCategory { get; } = isCategory;
        public ObservableCollection<Node> Children { get; } = new(children);
    }

    // Mirror of DashboardViewModel.FlattenExportableChannels: every non-category node.
    private static IEnumerable<Node> FlattenExportable(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsCategory)
                yield return node;

            foreach (var child in FlattenExportable(node.Children))
                yield return child;
        }
    }

    [AvaloniaFact]
    public void Programmatically_populating_the_bound_selection_selects_those_items_and_is_not_overwritten()
    {
        // A category with two channels, plus a channel that owns a nested thread —
        // the same shape as the dashboard's channel tree.
        var roots = new ObservableCollection<Node>
        {
            new Node(
                "General",
                isCategory: true,
                new Node("welcome", false),
                new Node("voice", false)
            ),
            new Node("announcements", isCategory: false, new Node("thread-a", false)),
        };

        // This is the collection the dashboard binds to (SelectedChannels) and that
        // ExportAsync reads. The control should adopt it, not replace it.
        var selected = new ObservableCollection<Node>();

        var tree = new TreeView
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = roots,
            ItemTemplate = new FuncTreeDataTemplate<Node>(
                (node, _) => new TextBlock { Text = node.Name },
                node => node.Children
            ),
        };

        // Equivalent to SelectedItems="{Binding SelectedChannels}" in the .axaml.
        tree.SelectedItems = selected;

        var window = new Window
        {
            Content = tree,
            Width = 300,
            Height = 300,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Expand categories so nested containers realize (the dashboard shows them expanded).
        foreach (var root in roots)
        {
            if (tree.TreeContainerFromItem(root) is TreeViewItem container)
                container.IsExpanded = true;
        }
        Dispatcher.UIThread.RunJobs();

        // Act — the "Select all" path: add every exportable (non-category) node.
        var exportable = FlattenExportable(roots).ToList();
        foreach (var node in exportable)
            selected.Add(node);

        Dispatcher.UIThread.RunJobs();

        // The control adopted our collection rather than swapping in its own.
        tree.SelectedItems.Should().BeSameAs(selected);

        // Our programmatic additions survived (not cleared by a re-sync) and excluded the category.
        selected.Should().HaveCount(exportable.Count);
        selected.Should().OnlyContain(node => !node.IsCategory);

        // The control registered the selection.
        tree.SelectedItem.Should().NotBeNull();

        // A realized, selected container reflects it visually (checkmark path).
        tree.TreeContainerFromItem(roots[1]).Should().BeOfType<TreeViewItem>();
        ((TreeViewItem)tree.TreeContainerFromItem(roots[1])!).IsSelected.Should().BeTrue();
    }
}
