using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Apex.Editor.Views;

/// <summary>
/// A file path as it reads: a GDT stores every separator doubled ("tbd\\s2\\car.xmod"), which says nothing to the
/// person looking at it. Display only; the stored value is never changed.
/// </summary>
public sealed class PathDisplay : IValueConverter
{
    public static readonly PathDisplay Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Contains("\\\\", StringComparison.Ordinal) ? s.Replace("\\\\", "\\") : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
