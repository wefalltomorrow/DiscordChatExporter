using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class ConversionViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceGuiConversion_" + Guid.NewGuid().ToString("N")
    );

    public ConversionViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string WriteJson(string fileName, string content, string inlineEmojis = "[]")
    {
        var path = Path.Combine(_dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $$"""
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": null
              },
              "dateRange": {
                "after": null,
                "before": null
              },
              "exportedAt": "1970-01-01T00:00:00.0000000+00:00",
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "{{content}}",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": {{inlineEmojis}}
                }
              ],
              "messageCount": 1
            }
            """
        );
        return path;
    }

    private string WriteInvalidJson(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{");
        return path;
    }

    private static ParsedExport CreateParsedExport(bool hasConversionDataBlock = false)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "test-channel",
            null,
            null,
            null,
            false,
            null
        );

        return new ParsedExport(
            guild,
            channel,
            [],
            hasConversionDataBlock ? new ConversionData([], [], []) : null,
            hasConversionDataBlock,
            null,
            null
        );
    }

    private ConversionViewModel CreateViewModel(
        Func<
            string,
            string,
            ExportFormat,
            CancellationToken,
            ValueTask<ExportResult>
        >? convertAsync = null,
        Func<string, CancellationToken, ValueTask<ParsedExport>>? parseAsync = null,
        Func<
            ParsedExport,
            string,
            string,
            ExportFormat,
            CancellationToken,
            ValueTask<ExportResult>
        >? convertParsedAsync = null,
        Func<string, bool>? outputFileExists = null,
        Func<string, StringComparer>? pathComparerForPath = null
    ) =>
        new(
            new DialogManager(),
            new LocalizationManager(new SettingsService()),
            convertAsync: convertAsync,
            parseAsync: parseAsync,
            convertParsedAsync: convertParsedAsync,
            outputFileExists: outputFileExists,
            pathComparerForPath: pathComparerForPath
        )
        {
            OutputFolderPath = _dir,
            IsHtmlDarkSelected = false,
        };

    [AvaloniaFact]
    public async Task Conversion_work_runs_off_the_ui_thread()
    {
        Dispatcher.UIThread.CheckAccess().Should().BeTrue();
        var ranOnUiThread = true;

        await ConversionViewModel.RunConversionWorkOffUiThreadAsync(() =>
        {
            ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            return ValueTask.FromResult(42);
        });

        ranOnUiThread.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Output_conflict_checks_run_off_the_ui_thread()
    {
        Dispatcher.UIThread.CheckAccess().Should().BeTrue();
        var json = WriteJson("chat.json", "from json");
        var ranOnUiThread = true;
        var viewModel = CreateViewModel(outputFileExists: _ =>
        {
            ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            return false;
        });
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        ranOnUiThread.Should().BeFalse();
    }

    [Fact]
    public void Cancel_operation_command_cancels_the_active_conversion_token()
    {
        var viewModel = CreateViewModel();
        var token = viewModel.BeginCancelableOperation();

        viewModel.CancelOperationCommand.CanExecute(null).Should().BeTrue();
        viewModel.CancelOperationCommand.Execute(null);

        token.IsCancellationRequested.Should().BeTrue();
        viewModel.CancelOperationCommand.CanExecute(null).Should().BeFalse();
        viewModel.EndCancelableOperation();
    }

    [Fact]
    public async Task Convert_records_failed_source_and_continues_with_remaining_sources()
    {
        var badJson = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(badJson, "{", TestContext.Current.CancellationToken);
        var goodJson = WriteJson("good.json", "hello from json");
        var viewModel = CreateViewModel();
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(badJson);
        viewModel.SourceFilePaths.Add(goodJson);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().Contain(r => r.SourceFilePath == badJson && !r.IsSuccess);
        viewModel.Results.Should().Contain(r => r.SourceFilePath == goodJson && r.IsSuccess);
        (
            await File.ReadAllTextAsync(
                Path.Combine(_dir, "good.csv"),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Contain("hello from json");
    }

    [Fact]
    public async Task Convert_uses_initial_sources_formats_and_output_folder()
    {
        var outputDir = Path.Combine(_dir, "output");
        var changedOutputDir = Path.Combine(_dir, "changed-output");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(changedOutputDir);

        var firstJson = WriteInvalidJson(Path.Combine("sources", "first.json"));
        var secondJson = WriteInvalidJson(Path.Combine("sources", "second.json"));
        var lateJson = WriteInvalidJson(Path.Combine("sources", "late.json"));

        var viewModel = CreateViewModel();
        viewModel.OutputFolderPath = outputDir;
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(firstJson);
        viewModel.SourceFilePaths.Add(secondJson);

        var changedLiveState = false;
        viewModel.Results.CollectionChanged += (_, _) =>
        {
            if (changedLiveState)
                return;

            changedLiveState = true;
            viewModel.OutputFolderPath = changedOutputDir;
            viewModel.IsCsvSelected = false;
            viewModel.IsTxtSelected = true;
            viewModel.SourceFilePaths.Add(lateJson);
        };

        var act = () => viewModel.ConvertCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => r.Format == ExportFormat.Csv);
        viewModel
            .Results.Should()
            .OnlyContain(r => Path.GetDirectoryName(r.OutputFilePath) == outputDir);
        viewModel.Results.Should().NotContain(r => r.SourceFilePath == lateJson);
    }

    [Fact]
    public async Task Convert_reports_conflicts_for_sources_that_would_write_same_output_path()
    {
        var firstJson = WriteJson(Path.Combine("first", "chat.json"), "from first json");
        var secondJson = WriteJson(Path.Combine("second", "chat.json"), "from second json");
        var viewModel = CreateViewModel();
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(firstJson);
        viewModel.SourceFilePaths.Add(secondJson);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => !r.IsSuccess);
        viewModel
            .Results.Should()
            .OnlyContain(r => r.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        viewModel
            .Results.Select(r => r.OutputFilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Should()
            .ContainSingle();
        File.Exists(Path.Combine(_dir, "chat.csv")).Should().BeFalse();
    }

    [Fact]
    public async Task Convert_allows_case_only_output_names_on_case_sensitive_targets()
    {
        var parsed = CreateParsedExport();
        var viewModel = CreateViewModel(
            parseAsync: (_, _) => ValueTask.FromResult(parsed),
            convertParsedAsync: (_, _, _, _, _) => ValueTask.FromResult(new ExportResult([], 0, 0)),
            outputFileExists: _ => false,
            pathComparerForPath: _ => StringComparer.Ordinal
        );
        viewModel.IsCsvSelected = true;
        // Source files live in different folders but share a base name differing only by case. Build
        // the paths with Path.Combine so GetFileNameWithoutExtension extracts "chat"/"CHAT" on every
        // OS — a hardcoded backslash path keeps the whole prefix on Linux, so the outputs never collide.
        viewModel.SourceFilePaths.Add(Path.Combine(_dir, "one", "chat.json"));
        viewModel.SourceFilePaths.Add(Path.Combine(_dir, "two", "CHAT.json"));

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => r.IsSuccess);
        viewModel.Results.Select(r => r.OutputFilePath).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Convert_reports_case_only_output_conflicts_on_case_insensitive_targets()
    {
        var parsed = CreateParsedExport();
        var viewModel = CreateViewModel(
            parseAsync: (_, _) => ValueTask.FromResult(parsed),
            convertParsedAsync: (_, _, _, _, _) => ValueTask.FromResult(new ExportResult([], 0, 0)),
            outputFileExists: _ => false,
            pathComparerForPath: _ => StringComparer.OrdinalIgnoreCase
        );
        viewModel.IsCsvSelected = true;
        // Source files live in different folders but share a base name differing only by case. Build
        // the paths with Path.Combine so GetFileNameWithoutExtension extracts "chat"/"CHAT" on every
        // OS — a hardcoded backslash path keeps the whole prefix on Linux, so the outputs never collide.
        viewModel.SourceFilePaths.Add(Path.Combine(_dir, "one", "chat.json"));
        viewModel.SourceFilePaths.Add(Path.Combine(_dir, "two", "CHAT.json"));

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => !r.IsSuccess);
        viewModel
            .Results.Should()
            .OnlyContain(r => r.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Convert_reports_conflict_when_output_file_already_exists()
    {
        var json = WriteJson("chat.json", "from json");
        var existingOutput = Path.Combine(_dir, "chat.csv");
        await File.WriteAllTextAsync(
            existingOutput,
            "do not replace",
            TestContext.Current.CancellationToken
        );
        var viewModel = CreateViewModel();
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().ContainSingle();
        viewModel.Results[0].IsSuccess.Should().BeFalse();
        viewModel.Results[0].Message.Should().ContainEquivalentOf("conflict");
        (await File.ReadAllTextAsync(existingOutput, TestContext.Current.CancellationToken))
            .Should()
            .Be("do not replace");
    }

    [Fact]
    public async Task Convert_parses_each_source_once_for_multiple_target_formats()
    {
        var json = WriteJson("chat.json", "from json");
        var parsed = CreateParsedExport(hasConversionDataBlock: true);
        var parseCount = 0;
        var convertCount = 0;
        var viewModel = CreateViewModel(
            parseAsync: (sourceFilePath, _) =>
            {
                sourceFilePath.Should().Be(json);
                parseCount++;
                return ValueTask.FromResult(parsed);
            },
            convertParsedAsync: (parsedExport, sourceFilePath, _, _, _) =>
            {
                parsedExport.Should().BeSameAs(parsed);
                sourceFilePath.Should().Be(json);
                convertCount++;
                return ValueTask.FromResult(new ExportResult([], 0, 0));
            }
        );
        viewModel.IsHtmlDarkSelected = true;
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        parseCount.Should().Be(1);
        convertCount.Should().Be(2);
        viewModel.Results.Should().HaveCount(2);
        viewModel.Results.Should().OnlyContain(r => r.IsSuccess && r.HasConversionData);
    }

    [Fact]
    public async Task Convert_deletes_partial_output_after_failed_conversion()
    {
        var json = WriteJson("chat.json", "from json");
        var outputPath = Path.Combine(_dir, "chat.csv");
        var shouldFail = true;
        var viewModel = CreateViewModel(
            convertAsync: (_, outputFilePath, _, _) =>
            {
                File.WriteAllText(outputFilePath, shouldFail ? "partial" : "complete");
                if (shouldFail)
                    throw new IOException("conversion failed");

                return ValueTask.FromResult(new ExportResult([], 0, 0));
            }
        );
        viewModel.IsCsvSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().ContainSingle().Which.IsSuccess.Should().BeFalse();
        File.Exists(outputPath).Should().BeFalse();

        shouldFail = false;
        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        File.ReadAllText(outputPath).Should().Be("complete");
    }

    [Fact]
    public async Task Convert_does_not_label_legacy_inline_emoji_metadata_as_conversion_data()
    {
        var json = WriteJson(
            "legacy.json",
            "hello <:wave:123>",
            """
            [
              {
                "id": "123",
                "name": "wave",
                "code": "<:wave:123>",
                "isAnimated": false,
                "imageUrl": "https://cdn.discordapp.com/emojis/123.png"
              }
            ]
            """
        );
        var viewModel = CreateViewModel();
        viewModel.IsTxtSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel.Results.Should().ContainSingle();
        viewModel.Results[0].IsSuccess.Should().BeTrue();
        viewModel.Results[0].HasConversionData.Should().BeFalse();
    }

    [Fact]
    public async Task Convert_writes_distinct_outputs_for_html_dark_and_light()
    {
        var json = WriteJson("chat.json", "hello html");
        var viewModel = CreateViewModel();
        viewModel.IsHtmlDarkSelected = true;
        viewModel.IsHtmlLightSelected = true;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        var htmlResults = viewModel.Results.Where(r =>
            r.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight
        );
        htmlResults.Select(r => r.OutputFilePath).Should().OnlyHaveUniqueItems();
        htmlResults.Should().HaveCount(2);
        foreach (var result in htmlResults)
            File.Exists(result.OutputFilePath).Should().BeTrue();
    }

    [Fact]
    public void Commands_that_mutate_conversion_state_are_disabled_while_busy()
    {
        var viewModel = CreateViewModel();
        viewModel.SourceFilePaths.Add(WriteJson("chat.json", "hello"));
        viewModel.IsCsvSelected = true;

        viewModel.IsBusy = true;

        viewModel.PickFilesCommand.CanExecute(null).Should().BeFalse();
        viewModel.PickOutputFolderCommand.CanExecute(null).Should().BeFalse();
        viewModel.ConvertCommand.CanExecute(null).Should().BeFalse();
        viewModel.NavigateBackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Convert_uses_plain_html_extension_when_only_one_html_format_is_selected()
    {
        var json = WriteJson("chat.json", "hello html");
        var viewModel = CreateViewModel();
        viewModel.IsHtmlDarkSelected = true;
        viewModel.IsHtmlLightSelected = false;
        viewModel.SourceFilePaths.Add(json);

        await viewModel.ConvertCommand.ExecuteAsync(null);

        viewModel
            .Results.Should()
            .ContainSingle()
            .Which.OutputFilePath.Should()
            .EndWith("chat.html");
        File.Exists(Path.Combine(_dir, "chat.html")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "chat.dark.html")).Should().BeFalse();
    }

    [Fact]
    public async Task Pick_output_folder_keeps_existing_path_when_picker_is_canceled()
    {
        const string ExistingOutputPath = "C:\\exports";
        var viewModel = new ConversionViewModel(
            new DialogManager(),
            new LocalizationManager(new SettingsService()),
            promptDirectoryPathAsync: _ => Task.FromResult<string?>(null),
            promptMultipleFilePathsAsync: _ =>
                Task.FromResult<IReadOnlyList<string>>([WriteJson("chat.json", "hello")])
        )
        {
            OutputFolderPath = ExistingOutputPath,
            IsHtmlDarkSelected = false,
        };

        await viewModel.PickOutputFolderCommand.ExecuteAsync(null);

        viewModel.OutputFolderPath.Should().Be(ExistingOutputPath);
    }

    [Fact]
    public async Task Pick_files_respects_case_sensitive_source_paths()
    {
        var viewModel = new ConversionViewModel(
            new DialogManager(),
            new LocalizationManager(new SettingsService()),
            promptMultipleFilePathsAsync: _ =>
                Task.FromResult<IReadOnlyList<string>>([
                    "C:\\sources\\chat.json",
                    "C:\\sources\\CHAT.json",
                ]),
            pathComparerForPath: _ => StringComparer.Ordinal
        )
        {
            IsHtmlDarkSelected = false,
        };

        await viewModel.PickFilesCommand.ExecuteAsync(null);

        viewModel.SourceFilePaths.Should().HaveCount(2);
    }
}
