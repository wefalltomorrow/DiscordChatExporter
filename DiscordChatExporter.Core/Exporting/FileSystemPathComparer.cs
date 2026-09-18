using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Provides path comparison helpers that respect the case-sensitivity of the target directory.
/// </summary>
public static class FileSystemPathComparer
{
    /// <summary>
    /// Gets a string comparer for paths in the same filesystem area as the specified file path.
    /// </summary>
    public static StringComparer GetComparerForPath(string filePath)
    {
        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var nearestExistingDirectoryPath = FindNearestExistingDirectory(directoryPath);

        return
            nearestExistingDirectoryPath is not null
            && IsCaseSensitiveDirectory(nearestExistingDirectoryPath)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    /// <summary>
    /// Checks whether a path collection contains the specified path using filesystem-aware comparison.
    /// </summary>
    public static bool ContainsPath(
        IEnumerable<string> paths,
        string path,
        Func<string, StringComparer> getComparerForPath
    )
    {
        var fullPath = Path.GetFullPath(path);
        return paths.Any(candidate =>
            getComparerForPath(fullPath).Equals(Path.GetFullPath(candidate), fullPath)
        );
    }

    /// <summary>
    /// Checks whether two paths resolve to the same filesystem path under the target comparer.
    /// </summary>
    public static bool AreSamePath(
        string firstPath,
        string secondPath,
        Func<string, StringComparer> getComparerForPath
    )
    {
        var fullFirstPath = Path.GetFullPath(firstPath);
        return getComparerForPath(fullFirstPath)
            .Equals(fullFirstPath, Path.GetFullPath(secondPath));
    }

    private static string? FindNearestExistingDirectory(string? directoryPath)
    {
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (Directory.Exists(directoryPath))
                return directoryPath;

            directoryPath = Path.GetDirectoryName(directoryPath);
        }

        return null;
    }

    private static bool IsCaseSensitiveDirectory(string directoryPath)
    {
        var probePath = Path.Combine(directoryPath, $".dceCaseProbe{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(probePath, "");
            return !File.Exists(ToggleAsciiCase(probePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* best-effort probe cleanup */
            }
        }
    }

    private static string ToggleAsciiCase(string text) =>
        new(
            text.Select(c =>
                    c switch
                    {
                        >= 'a' and <= 'z' => char.ToUpperInvariant(c),
                        >= 'A' and <= 'Z' => char.ToLowerInvariant(c),
                        _ => c,
                    }
                )
                .ToArray()
        );
}
