using System;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace DiscordChatExporter.Cli.Commands;

[Command("guide", Description = "Explains how to obtain the token, server or channel ID.")]
public partial class GuideCommand : ICommand
{
    public ValueTask ExecuteAsync(IConsole console)
    {
        // User token
        using (console.WithForegroundColor(ConsoleColor.White))
            console.Output.WriteLine("To get the token for your personal account:");

        console.Output.WriteLine(
            " *  Discord prohibits automating normal user accounts (self-bots) and says accounts found doing so may be terminated. Its Developer Policy also prohibits scraping data through Discord services. This app's personal-account mode automates a normal account to export messages. Request pacing and HTTP/TLS transport do not make this permitted or establish that your account is safe. If you need to avoid account restrictions or termination, do not use this mode."
        );
        console.Output.WriteLine(
            " *  Discord policies: https://support.discord.com/hc/en-us/articles/115002192352-Automated-User-Accounts-Self-Bots"
        );
        console.Output.WriteLine(
            "    https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy"
        );
        console.Output.WriteLine(" 1. Open Discord in your web browser and login");
        console.Output.WriteLine(" 2. Open any server or direct message channel");
        console.Output.WriteLine(" 3. Press Ctrl+Shift+I to show developer tools");
        console.Output.WriteLine(" 4. Navigate to the Network tab");
        console.Output.WriteLine(" 5. Press Ctrl+R to reload");
        console.Output.WriteLine(" 6. Switch between random channels to trigger network requests");
        console.Output.WriteLine(" 7. Search for a request that starts with \"messages\"");
        console.Output.WriteLine(" 8. Select the Headers tab on the right");
        console.Output.WriteLine(" 9. Scroll down to the Request Headers section");
        console.Output.WriteLine(" 10. Copy the value of the \"authorization\" header");
        console.Output.WriteLine();

        // Bot token
        using (console.WithForegroundColor(ConsoleColor.White))
            console.Output.WriteLine("To get the token for your bot:");

        console.Output.WriteLine(
            " The token is generated during bot creation. If you lost it, generate a new one:"
        );
        console.Output.WriteLine(" 1. Go to Discord developer portal");
        console.Output.WriteLine(" 2. Open your application's settings");
        console.Output.WriteLine(" 3. Navigate to the Bot section on the left");
        console.Output.WriteLine(" 4. Under Token click Reset Token");
        console.Output.WriteLine(" 5. Click Yes, do it! and authenticate to confirm");
        console.Output.WriteLine(
            " *  Integrations using the previous token will stop working until updated"
        );
        console.Output.WriteLine(
            " *  Your bot needs to have the Message Content Intent enabled to read messages"
        );
        console.Output.WriteLine();

        // Guild or channel ID
        using (console.WithForegroundColor(ConsoleColor.White))
            console.Output.WriteLine("To get the ID of a server or a channel:");

        console.Output.WriteLine(" 1. Open Discord");
        console.Output.WriteLine(" 2. Open Settings");
        console.Output.WriteLine(" 3. Go to Advanced section");
        console.Output.WriteLine(" 4. Enable Developer Mode");
        console.Output.WriteLine(
            " 5. Right-click on the desired server or channel and click Copy Server ID or Copy Channel ID"
        );
        console.Output.WriteLine();

        // Docs link
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            console.Output.WriteLine(
                "If you have questions or issues, please refer to the documentation:"
            );
        }

        using (console.WithForegroundColor(ConsoleColor.DarkCyan))
        {
            console.Output.WriteLine(
                "https://github.com/Tyrrrz/DiscordChatExporter/blob/prime/.docs"
            );
        }

        return default;
    }
}
