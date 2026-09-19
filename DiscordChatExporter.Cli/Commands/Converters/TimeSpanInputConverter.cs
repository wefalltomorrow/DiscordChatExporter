using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CliFx.Activation;

namespace DiscordChatExporter.Cli.Commands.Converters;

// Accepts shorthand durations such as '7d', '12h', '90m' or '45s', in addition to the
// 'd.hh:mm:ss' form that TimeSpan parses natively.
internal partial class TimeSpanInputConverter : ScalarInputConverter<TimeSpan>
{
    public override TimeSpan Convert(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return TimeSpan.Zero;

        var match = ShorthandRegex.Match(rawValue.Trim());
        if (match.Success)
        {
            var value = double.Parse(
                match.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture
            );

            return match.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => TimeSpan.FromDays(value),
                "h" => TimeSpan.FromHours(value),
                "m" => TimeSpan.FromMinutes(value),
                "s" => TimeSpan.FromSeconds(value),
                _ => throw new FormatException($"Invalid duration '{rawValue}'."),
            };
        }

        if (TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var timeSpan))
            return timeSpan;

        throw new FormatException(
            $"Invalid duration '{rawValue}'. Expected a value like '7d', '12h', '90m', or '1.00:00:00'."
        );
    }
}

internal partial class TimeSpanInputConverter
{
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([dhms])$", RegexOptions.IgnoreCase)]
    private static partial Regex ShorthandRegex { get; }
}
