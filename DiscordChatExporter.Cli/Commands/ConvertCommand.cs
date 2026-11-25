using System;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;

namespace DiscordChatExporter.Cli.Commands;

[Command("convert", Description = "Convert a local JSON transcript without contacting Discord.")]
public sealed partial class ConvertCommand : ICommand
{
    [CommandOption(
        "input",
        'i',
        Description = "Path to a DiscordChatExporter JSON export or Data Package messages.json file."
    )]
    public required string InputPath { get; set; }

    [CommandOption("output", 'o', Description = "Path for the converted output file.")]
    public required string OutputPath { get; set; }

    [CommandOption(
        "format",
        'f',
        Description = "Output format: HtmlDark, HtmlLight, PlainText, Csv, or Db."
    )]
    public string Format { get; set; } = nameof(ExportFormat.HtmlDark);

    public async ValueTask ExecuteAsync(IConsole console)
    {
        if (
            !Enum.TryParse<ExportFormat>(Format, ignoreCase: true, out var targetFormat)
            || !Enum.IsDefined(targetFormat)
            || targetFormat is ExportFormat.Json
        )
        {
            throw new ArgumentException(
                "Unsupported output format. Choose HtmlDark, HtmlLight, PlainText, Csv, or Db."
            );
        }

        var result = await ExportConverter.ConvertAsync(InputPath, OutputPath, targetFormat);

        console.Output.WriteLine(
            $"Converted {result.MessageCount:N0} message(s) to '{OutputPath}'."
        );
    }
}
