using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using DiscordChatExporter.Gui.Utils.Extensions;

namespace DiscordChatExporter.Gui.Utils;

// Best-effort "export finished" attention cue. On Windows, flashes the taskbar button when the
// main window isn't focused; no-op everywhere else. No dependency, works for a portable exe.
internal static class CompletionAttention
{
    internal readonly record struct Target(bool IsActive, Func<IntPtr?> GetHandle);

    public static void FlashIfUnfocused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        FlashIfUnfocusedCore(
            () =>
                Application.Current?.ApplicationLifetime?.TryGetTopLevel() is Window window
                    ? new Target(window.IsActive, () => window.TryGetPlatformHandle()?.Handle)
                    : null,
            FlashTaskbar
        );
    }

    internal static void FlashIfUnfocusedCore(Func<Target?> getTarget, Action<IntPtr> flashTaskbar)
    {
        try
        {
            // Only signal if the user isn't already looking at the window.
            var target = getTarget();
            if (target is null || target.Value.IsActive)
                return;

            var handle = target.Value.GetHandle();
            if (handle is null || handle == IntPtr.Zero)
                return;

            flashTaskbar(handle.Value);
        }
        catch
        {
            // Best-effort: an attention cue must never disrupt the app.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void FlashTaskbar(IntPtr hwnd)
    {
        var info = new NativeMethods.Windows.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.Windows.FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = NativeMethods.Windows.FLASHW_TRAY | NativeMethods.Windows.FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };

        NativeMethods.Windows.FlashWindowEx(ref info);
    }
}
