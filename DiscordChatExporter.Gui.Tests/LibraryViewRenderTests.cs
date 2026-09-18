using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using DiscordChatExporter.Gui.Views.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordChatExporter.Gui.Tests;

// Headless render-smoke test for the Library view (Task 5). The Library UI has logic tests but
// had never actually been rendered: this proves LibraryView's XAML inflates against a populated
// LibraryViewModel without throwing, that the catalog/result item templates realize, and that
// their (uncompiled, reflection-resolved) bindings actually resolve to the data we set.
//
// Why this catches more than a compile pass: the inner DataTemplates in LibraryView.axaml carry
// no x:DataType, so their bindings (File, Hit.Snippet, GuildName, ...) are runtime/reflection
// bindings that the XAML compiler does NOT verify. A failed binding does not throw — Avalonia
// logs a warning and leaves the target unset — so asserting "no exception" alone would miss the
// exact bug class this test exists for. We therefore assert realized bound *values*.
public sealed class LibraryViewRenderTests
{
    // Minimal DI graph mirroring App.axaml.cs / MainViewNavigationTests, enough to resolve a real
    // LibraryViewModel (its three deps — SettingsService, DialogManager, LocalizationManager — are
    // all registered here).
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
        services.AddTransient<ExportSetupViewModel>();
        services.AddTransient<MessageBoxViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(true);
    }

    private static ManifestEntry SampleEntry(
        string guildName,
        string channelName,
        string file,
        string format
    ) =>
        new(
            GuildId: "111",
            GuildName: guildName,
            ChannelId: "222",
            ChannelName: channelName,
            CategoryName: "General",
            File: file,
            Format: format,
            MessageCount: 42,
            FirstMessageId: "1",
            FirstMessageTimestamp: DateTimeOffset.UnixEpoch,
            LastMessageId: "2",
            LastMessageTimestamp: DateTimeOffset.UnixEpoch.AddDays(1),
            AssetCount: 3,
            FileSizeBytes: 1024,
            Sha256: "deadbeef",
            Partitioned: false,
            ExportedAt: DateTimeOffset.UnixEpoch
        );

    [AvaloniaFact]
    public void LibraryView_renders_a_populated_view_model_without_throwing()
    {
        using var provider = BuildServices();

        // Resolve a real LibraryViewModel the same way navigation does. Do NOT InitializeAsync():
        // that would hit ExportCatalogBuilder against real directories. We populate the public
        // collections directly so the templates have data to render.
        var viewModel = provider.GetRequiredService<LibraryViewModel>();

        // 1-2 catalog rows; one is a searchable .db so HasSearchableExports has a basis.
        const string DbFile = @"C:\exports\guild\general.db";
        viewModel.Entries.Add(SampleEntry("Test Guild", "general", DbFile, "db"));
        viewModel.Entries.Add(
            SampleEntry("Test Guild", "off-topic", @"C:\exports\guild\off-topic.html", "HtmlDark")
        );

        // One search result wrapping a hit + its source entry (exercises the result template's
        // nested Hit.* bindings and the SourceLabel computed off Source).
        const string Snippet = "the quick brown fox jumped over the lazy dog";
        var hit = new SqliteSearchHit(
            DatabaseFilePath: DbFile,
            MessageId: "9001",
            Timestamp: "2024-01-01T00:00:00Z",
            AuthorName: "alice",
            Snippet: Snippet
        );
        viewModel.SearchResults.Add(new LibrarySearchResult(hit, viewModel.Entries[0]));

        // Exercise the mutually-exclusive empty-state visibility bindings: with results present we
        // want the catalog + results lists visible and the empty-state hints suppressed.
        viewModel.HasSearchableExports = true;
        viewModel.ShowNoResults = false;
        viewModel.ShowSearchUnavailable = false;

        // LibraryView decorates itself with Material.Styles control themes (Card, SoloTextBox,
        // MaterialFlatButton) and Material brushes (MaterialDarkBackgroundBrush, PrimaryHueMidBrush,
        // ...) plus MaterialIcon, all referenced via {DynamicResource ...}. Hosting the real
        // MaterialTheme in a headless test outside App's XAML pipeline is infeasible: adding it to a
        // Styles collection eagerly populates its nested styles, whose internal
        // StaticResource MaterialPrimaryMidBrush is not resolvable at that point and throws
        // (KeyNotFoundException) — a harness limitation, not a defect in LibraryView. So we render
        // under the FluentTheme the shared headless TestApp already provides. The Material refs are
        // all DynamicResource, and an UNRESOLVED DynamicResource does NOT throw — it leaves the
        // property at its default — so the view still inflates, the item templates still realize, and
        // (the real point of this test) the reflection-resolved data bindings still resolve. What we
        // forgo is the visual styling from third-party control themes, which is not the code under
        // test. This is the task's sanctioned "view builds + binds + no throw" fallback, kept
        // stronger by retaining the bound-value assertions below.
        var view = new LibraryView { DataContext = viewModel };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };

        // Act: showing + pumping the dispatcher forces the layout pass that inflates templates and
        // realizes item containers. If the production XAML had a runtime bug, this throws here.
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // --- Assertions ---

        // The two catalog ListBoxes/TextBox are realized in the visual tree.
        var listBoxes = view.GetVisualDescendants().OfType<ListBox>().ToList();
        listBoxes
            .Should()
            .HaveCountGreaterThanOrEqualTo(
                2,
                "the catalog list and the results list should both be realized"
            );

        var searchTextBox = view.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.Name == "SearchTextBox");
        searchTextBox.Should().NotBeNull("the named search TextBox should be realized");

        // The InnerRightContent button (the search-submit arrow) is realized inside the TextBox.
        // Discriminate on the bound command (reference equality with the VM's generated SearchCommand)
        // so this proves both that it's the right button AND that Command="{Binding SearchCommand}"
        // resolved — rather than matching any button the TextBox template might realize.
        searchTextBox!
            .GetVisualDescendants()
            .OfType<Button>()
            .Should()
            .Contain(
                b => b.Command == viewModel.SearchCommand,
                "the InnerRightContent submit button binds SearchCommand"
            );

        // The catalog ListBox realized a container per populated entry.
        var catalogListBox = listBoxes.First(lb => lb.ItemCount == viewModel.Entries.Count);
        catalogListBox.GetRealizedContainers().Should().NotBeEmpty();

        // Binding-resolution proof (the real point of this test): the catalog item template's
        // single-binding TextBlock (Text="{Binding File}") shows the file path we set, and the
        // result item template's nested binding (Text="{Binding Hit.Snippet}") shows the snippet.
        // These are the uncompiled bindings the XAML compiler never checked.
        var renderedTexts = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(tb => tb.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        renderedTexts
            .Should()
            .Contain(DbFile, "the catalog item template's File binding should resolve and render");
        renderedTexts
            .Should()
            .Contain(
                Snippet,
                "the result item template's nested Hit.Snippet binding should resolve and render"
            );
    }
}
