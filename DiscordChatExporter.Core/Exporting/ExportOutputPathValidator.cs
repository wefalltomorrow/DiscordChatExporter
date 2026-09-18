using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

public static class ExportOutputPathValidator
{
    public static IReadOnlyList<string> GetDuplicateOutputFilePaths(
        IEnumerable<ExportRequest> requests
    ) =>
        GetDuplicateOutputFilePaths(
            requests.Select(r => r.OutputFilePath),
            FileSystemPathComparer.GetComparerForPath
        );

    public static IReadOnlyList<string> GetDuplicateOutputFilePaths(
        IEnumerable<string> outputFilePaths,
        Func<string, StringComparer> getComparerForPath
    )
    {
        var paths = outputFilePaths.Select(Path.GetFullPath).ToArray();
        var duplicatePaths = new List<string>();

        for (var i = 0; i < paths.Length; i++)
        {
            for (var j = i + 1; j < paths.Length; j++)
            {
                if (!FileSystemPathComparer.AreSamePath(paths[i], paths[j], getComparerForPath))
                    continue;

                duplicatePaths.Add(paths[i]);
                duplicatePaths.Add(paths[j]);
            }
        }

        return duplicatePaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }
}
