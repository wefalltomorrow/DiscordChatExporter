using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class FileNameChannelId
{
    [GeneratedRegex(@"\[([0-9]+)\]")]
    private static partial Regex IdRegex();

    public static Snowflake? TryParse(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var matches = IdRegex().Matches(name);
        var match = matches.Count > 0 ? matches[^1] : null;
        return match?.Success == true
            ? Snowflake.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }
}
