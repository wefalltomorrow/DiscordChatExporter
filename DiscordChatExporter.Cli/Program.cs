using System.Threading.Tasks;
using CliFx;

namespace DiscordChatExporter.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args) =>
        await new CommandLineApplicationBuilder()
            .AddCommand(Commands.ConvertCommand.Descriptor)
            .AddCommand(Commands.GuideCommand.Descriptor)
            .Build()
            .RunAsync(args);
}
