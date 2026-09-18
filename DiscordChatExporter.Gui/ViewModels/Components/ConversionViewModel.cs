using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public sealed partial class ConversionViewModel(
    DialogManager dialogManager,
    LocalizationManager localizationManager,
    Func<FilePickerFileType[], Task<IReadOnlyList<string>>>? promptMultipleFilePathsAsync = null,
    Func<string, Task<string?>>? promptDirectoryPathAsync = null,
    Func<string, string, ExportFormat, CancellationToken, ValueTask<ExportResult>>? convertAsync =
        null,
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
) : ViewModelBase
{
    private readonly Func<
        FilePickerFileType[],
        Task<IReadOnlyList<string>>
    > _promptMultipleFilePathsAsync =
        promptMultipleFilePathsAsync
        ?? (fileTypes => dialogManager.PromptMultipleFilePathsAsync(fileTypes));
    private readonly Func<string, Task<string?>> _promptDirectoryPathAsync =
        promptDirectoryPathAsync
        ?? (defaultDirPath => dialogManager.PromptDirectoryPathAsync(defaultDirPath));
    private readonly Func<string, CancellationToken, ValueTask<ParsedExport>> _parseAsync =
        parseAsync ?? JsonExportReader.ParseAsync;
    private readonly Func<
        ParsedExport,
        string,
        string,
        ExportFormat,
        CancellationToken,
        ValueTask<ExportResult>
    > _convertParsedAsync =
        convertParsedAsync
        ?? (
            convertAsync is not null
                ? (_, sourceFilePath, outputFilePath, format, cancellationToken) =>
                    convertAsync(sourceFilePath, outputFilePath, format, cancellationToken)
                : ExportConverter.ConvertAsync
        );
    private readonly Func<string, bool> _outputFileExists = outputFileExists ?? File.Exists;
    private readonly Func<string, StringComparer> _pathComparerForPath =
        pathComparerForPath ?? FileSystemPathComparer.GetComparerForPath;

    private const string ConvertedMessage = "Converted";
    private const string CanceledMessage = "Canceled";
    private const string OutputPathConflictMessage = "Output path conflict";

    private CancellationTokenSource? _operationCancellation;

    public LocalizationManager LocalizationManager { get; } = localizationManager;

    public event EventHandler? BackRequested;

    public ObservableCollection<string> SourceFilePaths { get; } = [];

    public ObservableCollection<ConversionResultRow> Results { get; } = [];

    public bool CanCancelOperation => _operationCancellation is { IsCancellationRequested: false };

    internal CancellationToken BeginCancelableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
        return _operationCancellation.Token;
    }

    internal void EndCancelableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    internal static Task<T> RunConversionWorkOffUiThreadAsync<T>(
        Func<ValueTask<T>> workAsync,
        CancellationToken cancellationToken = default
    ) => Task.Run(async () => await workAsync(), cancellationToken);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial string? OutputFolderPath { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsHtmlDarkSelected { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsHtmlLightSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsCsvSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsTxtSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    public partial bool IsSqliteSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateBackCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditConversionState))]
    public partial bool IsBusy { get; set; }

    public bool CanEditConversionState => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEditConversionState))]
    private async Task PickFilesAsync()
    {
        if (IsBusy)
            return;

        var paths = await _promptMultipleFilePathsAsync([
            new FilePickerFileType("JSON exports") { Patterns = ["*.json"] },
        ]);

        if (IsBusy)
            return;

        foreach (
            var path in paths.Where(p =>
                !FileSystemPathComparer.ContainsPath(SourceFilePaths, p, _pathComparerForPath)
            )
        )
            SourceFilePaths.Add(path);

        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditConversionState))]
    private async Task PickOutputFolderAsync()
    {
        if (IsBusy)
            return;

        var outputFolderPath = await _promptDirectoryPathAsync(OutputFolderPath ?? "");
        if (IsBusy || outputFolderPath is null)
            return;

        OutputFolderPath = outputFolderPath;
    }

    private bool CanConvert() =>
        !IsBusy
        && SourceFilePaths.Count > 0
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && GetTargetFormats().Any();

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        if (IsBusy)
            return;

        var outputFolderPath = OutputFolderPath;
        var sourceFilePaths = SourceFilePaths.ToArray();
        var targetFormats = GetTargetFormats().ToArray();

        if (
            string.IsNullOrWhiteSpace(outputFolderPath)
            || sourceFilePaths.Length <= 0
            || targetFormats.Length <= 0
        )
            return;

        IsBusy = true;
        Results.Clear();
        var cancellationToken = BeginCancelableOperation();
        try
        {
            var plan = await RunConversionWorkOffUiThreadAsync(
                () =>
                    ValueTask.FromResult(
                        CreateConversionPlan(
                            sourceFilePaths,
                            targetFormats,
                            outputFolderPath,
                            _outputFileExists,
                            _pathComparerForPath
                        )
                    ),
                cancellationToken
            );

            foreach (var sourceJobs in plan.Jobs.GroupBy(j => j.SourceIndex))
            {
                var sourcePath = sourceJobs.First().SourceFilePath;
                var conflictJobs = sourceJobs
                    .Where(j => plan.HasOutputConflict(j.OutputFilePath, _pathComparerForPath))
                    .ToArray();
                var remainingJobs = sourceJobs
                    .Where(j => !plan.HasOutputConflict(j.OutputFilePath, _pathComparerForPath))
                    .ToArray();

                foreach (var job in conflictJobs)
                {
                    Results.Add(
                        new ConversionResultRow(
                            job.SourceFilePath,
                            job.OutputFilePath,
                            job.Format,
                            false,
                            false,
                            OutputPathConflictMessage
                        )
                    );
                }

                if (remainingJobs.Length <= 0)
                    continue;

                ParsedExport parsed;
                try
                {
                    parsed = await RunConversionWorkOffUiThreadAsync(
                        () => _parseAsync(sourcePath, cancellationToken),
                        cancellationToken
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var job in remainingJobs)
                    {
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
                                false,
                                false,
                                ex.Message
                            )
                        );
                    }

                    continue;
                }

                var hasConversionData = parsed.HasConversionDataBlock;

                foreach (var job in remainingJobs)
                {
                    try
                    {
                        await RunConversionWorkOffUiThreadAsync(
                            () =>
                                _convertParsedAsync(
                                    parsed,
                                    job.SourceFilePath,
                                    job.OutputFilePath,
                                    job.Format,
                                    cancellationToken
                                ),
                            cancellationToken
                        );
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
                                hasConversionData,
                                true,
                                ConvertedMessage
                            )
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        DeletePartialOutputFile(job.OutputFilePath);
                        Results.Add(
                            new ConversionResultRow(
                                job.SourceFilePath,
                                job.OutputFilePath,
                                job.Format,
                                hasConversionData,
                                false,
                                ex.Message
                            )
                        );
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Results.Add(
                new ConversionResultRow("", "", ExportFormat.Json, false, false, CanceledMessage)
            );
        }
        finally
        {
            EndCancelableOperation();
            IsBusy = false;
        }
    }

    private static void DeletePartialOutputFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: keep the original conversion failure visible.
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        OnPropertyChanged(nameof(CanCancelOperation));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditConversionState))]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private IEnumerable<ExportFormat> GetTargetFormats()
    {
        if (IsHtmlDarkSelected)
            yield return ExportFormat.HtmlDark;
        if (IsHtmlLightSelected)
            yield return ExportFormat.HtmlLight;
        if (IsCsvSelected)
            yield return ExportFormat.Csv;
        if (IsTxtSelected)
            yield return ExportFormat.PlainText;
        if (IsSqliteSelected)
            yield return ExportFormat.Db;
    }

    private static IReadOnlyList<ConversionJob> CreateConversionJobs(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<ExportFormat> targetFormats,
        string outputFolderPath
    )
    {
        var disambiguateHtml =
            targetFormats.Contains(ExportFormat.HtmlDark)
            && targetFormats.Contains(ExportFormat.HtmlLight);

        return sourceFilePaths
            .SelectMany(
                (sourcePath, sourceIndex) =>
                    targetFormats.Select(format => new ConversionJob(
                        sourceIndex,
                        sourcePath,
                        GetOutputPath(outputFolderPath, sourcePath, format, disambiguateHtml),
                        format
                    ))
            )
            .ToArray();
    }

    private static ConversionPlan CreateConversionPlan(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<ExportFormat> targetFormats,
        string outputFolderPath,
        Func<string, bool> outputFileExists,
        Func<string, StringComparer> pathComparerForPath
    )
    {
        var conversionJobs = CreateConversionJobs(sourceFilePaths, targetFormats, outputFolderPath);
        var duplicateOutputPaths = ExportOutputPathValidator.GetDuplicateOutputFilePaths(
            conversionJobs.Select(j => j.OutputFilePath),
            pathComparerForPath
        );
        var existingOutputPaths = conversionJobs
            .Select(j => j.OutputFilePath)
            .Where(outputFileExists);
        var conflictingOutputPaths = duplicateOutputPaths
            .Concat(existingOutputPaths)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ConversionPlan(conversionJobs, conflictingOutputPaths);
    }

    private static string GetOutputPath(
        string outputFolderPath,
        string sourcePath,
        ExportFormat format,
        bool disambiguateHtml
    )
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        var formatSuffix = disambiguateHtml
            ? format switch
            {
                ExportFormat.HtmlDark => ".dark",
                ExportFormat.HtmlLight => ".light",
                _ => "",
            }
            : "";

        return Path.Combine(
            outputFolderPath,
            fileNameWithoutExtension + formatSuffix + "." + format.GetFileExtension()
        );
    }

    private sealed record ConversionJob(
        int SourceIndex,
        string SourceFilePath,
        string OutputFilePath,
        ExportFormat Format
    );

    private sealed record ConversionPlan(
        IReadOnlyList<ConversionJob> Jobs,
        IReadOnlyList<string> ConflictingOutputPaths
    )
    {
        public bool HasOutputConflict(
            string outputFilePath,
            Func<string, StringComparer> pathComparerForPath
        ) =>
            FileSystemPathComparer.ContainsPath(
                ConflictingOutputPaths,
                outputFilePath,
                pathComparerForPath
            );
    }
}

public sealed record ConversionResultRow(
    string SourceFilePath,
    string OutputFilePath,
    ExportFormat Format,
    bool HasConversionData,
    bool IsSuccess,
    string Message
)
{
    public string FormatName => Format.GetDisplayName();

    public string FidelityText => HasConversionData ? "Full fidelity" : "Best effort";
}
