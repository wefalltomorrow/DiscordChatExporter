using System.Reflection;
using System.Runtime.InteropServices;
using DiscordChatExporter.Gui.Utils;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public class NativeMethodsTests
{
    [Fact]
    public void Windows_message_box_uses_the_unicode_entrypoint()
    {
        var method = typeof(NativeMethods.Windows).GetMethod(
            nameof(NativeMethods.Windows.MessageBox),
            BindingFlags.Public | BindingFlags.Static
        );

        var attribute = method!.GetCustomAttribute<DllImportAttribute>();

        attribute.Should().NotBeNull();
        attribute!.EntryPoint.Should().Be("MessageBoxW");
        attribute.CharSet.Should().Be(CharSet.Unicode);
    }
}
