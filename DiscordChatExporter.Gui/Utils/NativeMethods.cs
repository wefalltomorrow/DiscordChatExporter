using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiscordChatExporter.Gui.Utils;

internal static class NativeMethods
{
    public static class Windows
    {
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-flashwinfo
        public const uint FLASHW_TRAY = 0x00000002; // flash the taskbar button
        public const uint FLASHW_TIMERNOFG = 0x0000000C; // flash until the window comes to the foreground

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport(
            "user32.dll",
            EntryPoint = "MessageBoxW",
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        public static extern int MessageBox(nint hWnd, string text, string caption, uint type);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    }
}
