using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CarHunt.Converters;

/// <summary>
/// Shows whether a sort option is the one currently chosen in its category.
///
/// Display only — the choice is written by SetSortCommand instead. Writing back from
/// here does not work: ticking one option unticks its opposite, and that untick then
/// wrote an empty value back, wiping out the choice just made.
/// </summary>
public class SortSelectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
