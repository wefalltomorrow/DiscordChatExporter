using System.IO;
using System.Text.RegularExpressions;

namespace DiscordChatExporter.Core.Exporting.Continuation;

internal static partial class ContinuationFileName
{
    [GeneratedRegex(
        @"\[[0-9]+\]\s*\((?:before\s+[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{4}-[0-9]{2}-[0-9]{2}\s+to\s+[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{4}-[0-9]{2}-[0-9]{2})\)(?:\s+\[part\s+[0-9]+\])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BeforeBoundHintRegex();

    public static bool HasBeforeBoundHint(string filePath) =>
        BeforeBoundHintRegex().IsMatch(Path.GetFileNameWithoutExtension(filePath));
}
