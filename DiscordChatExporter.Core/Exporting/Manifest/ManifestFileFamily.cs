using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DiscordChatExporter.Core.Exporting.Manifest;

internal static partial class ManifestFileFamily
{
    [GeneratedRegex(@" \[part (\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PartitionSuffixRegex();

    public static string GetBaseFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var match = PartitionSuffixRegex().Match(nameWithoutExtension);

        return match.Success && match.Index + match.Length == nameWithoutExtension.Length
            ? nameWithoutExtension[..match.Index] + extension
            : fileName;
    }

    public static bool IsSameFamily(string left, string right) =>
        string.Equals(
            GetBaseFileName(left),
            GetBaseFileName(right),
            StringComparison.OrdinalIgnoreCase
        );
}
