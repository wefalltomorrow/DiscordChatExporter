using System;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace DiscordChatExporter.Cli.Commands;

[Command("guide", Description = "Explains how to convert local chat transcript files.")]
public partial class GuideCommand : ICommand
{
    public ValueTask ExecuteAsync(IConsole console)
    {
        using (console.WithForegroundColor(ConsoleColor.White))
            console.Output.WriteLine(
                "This build works with local files and does not connect to Discord."
            );

        console.Output.WriteLine(
            "Convert one DiscordChatExporter JSON export or Data Package messages.json transcript:"
        );
        console.Output.WriteLine(
            "  DiscordChatExporter.Cli convert --input messages.json --output conversation.html --format HtmlDark"
        );
        console.Output.WriteLine("Supported formats: HtmlDark, HtmlLight, PlainText, Csv, and Db.");
        console.Output.WriteLine(
            "For batch conversion of an extracted Data Package, use the GUI Conversion screen."
        );
        console.Output.WriteLine();

        console.Output.WriteLine("Documentation:");

        using (console.WithForegroundColor(ConsoleColor.DarkCyan))
        {
            console.Output.WriteLine(
                "https://github.com/wefalltomorrow/DiscordChatExporter/tree/prime/.docs"
            );
        }

        return default;
    }
}
