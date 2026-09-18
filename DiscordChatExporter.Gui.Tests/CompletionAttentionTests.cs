using System;
using DiscordChatExporter.Gui.Utils;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public class CompletionAttentionTests
{
    [Fact]
    public void Flash_if_unfocused_swallows_top_level_lookup_failures()
    {
        var act = () =>
            CompletionAttention.FlashIfUnfocusedCore(
                () => throw new InvalidOperationException("top level failed"),
                _ => { }
            );

        act.Should().NotThrow();
    }

    [Fact]
    public void Flash_if_unfocused_swallows_handle_lookup_failures()
    {
        var act = () =>
            CompletionAttention.FlashIfUnfocusedCore(
                () =>
                    new CompletionAttention.Target(
                        IsActive: false,
                        GetHandle: () => throw new InvalidOperationException("handle failed")
                    ),
                _ => { }
            );

        act.Should().NotThrow();
    }

    [Fact]
    public void Flash_if_unfocused_swallows_flash_failures()
    {
        var act = () =>
            CompletionAttention.FlashIfUnfocusedCore(
                () =>
                    new CompletionAttention.Target(
                        IsActive: false,
                        GetHandle: () => new IntPtr(123)
                    ),
                _ => throw new InvalidOperationException("flash failed")
            );

        act.Should().NotThrow();
    }
}
