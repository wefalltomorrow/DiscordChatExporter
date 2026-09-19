using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Utils;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Spectre.Console;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

// These need no token and no network: they drive Spectre's interactive renderer against an
// in-memory writer, which is the only way to see the progress columns at all. A redirected
// console falls back to a renderer that prints just the description and the percentage.
public partial class ProgressDisplaySpecs
{
    private static async Task<string> RenderAsync(ExportStats stats, double progress)
    {
        var writer = new StringWriter();

        var console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                // Spectre only uses the column renderer when the console is both ANSI-capable
                // and interactive; anything else falls back to printing the description and
                // the percentage alone
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.Yes,
                Out = new AnsiConsoleOutput(writer),
            }
        );

        // Wide enough that no column has to be truncated
        console.Profile.Width = 200;

        var column = new ExportStatsColumn();

        await console
            .Progress()
            .AutoClear(false)
            .AutoRefresh(false)
            .Columns(new TaskDescriptionColumn(), new PercentageColumn(), column)
            .StartAsync(ctx =>
            {
                var task = ctx.AddTask(
                    "Chat / nplusplus",
                    new ProgressTaskSettings { MaxValue = 1 }
                );
                column.Attach(task, stats);
                task.Value = progress;
                ctx.Refresh();
                return Task.CompletedTask;
            });

        // Strip the cursor movement and styling escapes that the ANSI renderer emits
        return AnsiEscapeRegex().Replace(writer.ToString(), "");
    }

    [GeneratedRegex("""\e\[[0-9;?]*[a-zA-Z]""")]
    private static partial Regex AnsiEscapeRegex();

    [Fact]
    public async Task I_can_see_the_export_counters_in_place_of_a_progress_bar()
    {
        // Arrange
        var stats = new ExportStats { TotalMessages = 5678 };
        stats.ReportExported(1234, 3_500_000);
        stats.ReportDownloadedBytes(650_000);

        for (var i = 0; i < 142; i++)
            stats.ReportRequest();

        // Act
        var output = await RenderAsync(stats, 0.63);

        // Assert
        output.Should().Contain("Chat / nplusplus");
        output.Should().Contain("63%");
        output.Should().Contain("1,234/5,678 msg");
        output.Should().Contain("634.8 KiB in");
        output.Should().Contain("3.3 MiB out");
        output.Should().Contain("142 req");

        // The bar was made of these
        output.Should().NotContain("━");
        output.Should().NotContain("-----");
    }

    [Fact]
    public async Task I_can_see_the_message_count_without_a_total_when_Discord_does_not_report_one()
    {
        // Arrange
        var stats = new ExportStats();
        stats.ReportExported(98_765, 1024);
        stats.ReportDownloadedBytes(2_000_000_000);

        // Act
        var output = await RenderAsync(stats, 0.5);

        // Assert
        output.Should().Contain("98,765 msg");
        output.Should().NotContain("98,765/");
        output.Should().Contain("1.9 GiB in");
        output.Should().Contain("1.0 KiB out");
        output.Should().Contain("0 req");
    }
}
