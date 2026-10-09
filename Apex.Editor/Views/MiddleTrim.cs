using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Apex.Editor.Views;

/// <summary>
/// Shortens a long asset name in the middle ("wpn_ar_hav…_zm_upgraded"): names in one family share
/// their start and differ at the end, so trimming the tail would make sibling tabs identical.
/// </summary>
public sealed class MiddleTrim : IValueConverter
{
    public static readonly MiddleTrim Tab = new() { MaxChars = 26 };

    public int MaxChars { get; init; } = 26;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length <= MaxChars)
            return value;
        var tail = (MaxChars - 1) / 2;
        var head = MaxChars - 1 - tail;
        return string.Concat(s.AsSpan(0, head), "…", s.AsSpan(s.Length - tail));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
