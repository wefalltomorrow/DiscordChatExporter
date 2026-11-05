using System.Threading.Tasks;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord.Data;
using PowerKit.Extensions;

namespace DiscordChatExporter.Cli.Commands;

[Command("exportdm", Description = "Exports all direct message channels.")]
public partial class ExportDirectMessagesCommand : ExportCommandBase
{
    protected override async ValueTask ExecuteCoreAsync(IConsole console)
    {
        await base.ExecuteCoreAsync(console);

        var cancellationToken = console.RegisterCancellationHandler();

        await console.Output.WriteLineAsync("Fetching channels...");
        var channels = await Discord.GetGuildChannelsAsync(
            Guild.DirectMessages.Id,
            cancellationToken
        );

        await ExportAsync(console, channels);
    }
}
