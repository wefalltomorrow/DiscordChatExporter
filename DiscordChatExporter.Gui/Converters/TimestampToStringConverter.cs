using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DiscordChatExporter.Gui.Converters;

public class TimestampToStringConverter : IValueConverter
{
    public static TimestampToStringConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) =>
        value switch
        {
            DateTimeOffset timestamp => Format(timestamp, culture),
            DateTime timestamp => timestamp.ToString("g", culture),
            string timestamp
                when DateTimeOffset.TryParse(
                    timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed
                ) => Format(parsed, culture),
            string timestamp => timestamp,
            _ => default,
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();

    private static string Format(DateTimeOffset timestamp, CultureInfo culture) =>
        timestamp.ToString("g", culture);
}
