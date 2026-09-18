using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DiscordChatExporter.Gui.Framework;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class ViewManagerTests
{
    private sealed class CountingViewModel : ViewModelBase
    {
        public int InitializeCount { get; private set; }

        public override Task InitializeAsync()
        {
            InitializeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingViewModel : ViewModelBase
    {
        public override Task InitializeAsync() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Initialize_view_model_once_does_not_run_initialize_twice()
    {
        var manager = new ViewManager();
        var viewModel = new CountingViewModel();

        await manager.InitializeViewModelOnceAsync(viewModel);
        await manager.InitializeViewModelOnceAsync(viewModel);

        viewModel.InitializeCount.Should().Be(1);
    }

    [Fact]
    public async Task Initialize_view_model_once_contains_initialize_failures()
    {
        var manager = new ViewManager();
        var viewModel = new ThrowingViewModel();

        var act = async () => await manager.InitializeViewModelOnceAsync(viewModel);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Initialized_view_models_are_not_retained_forever()
    {
        var viewManager = new ViewManager();
        var reference = await InitializeAndReleaseAsync(viewManager);

        CollectGarbage();

        reference.IsAlive.Should().BeFalse();
        GC.KeepAlive(viewManager);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> InitializeAndReleaseAsync(ViewManager viewManager)
    {
        var viewModel = new CountingViewModel();

        await viewManager.InitializeViewModelOnceAsync(viewModel);

        return new WeakReference(viewModel);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
