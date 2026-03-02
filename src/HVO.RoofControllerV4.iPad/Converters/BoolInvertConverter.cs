using System.Globalization;
using Microsoft.Maui.Controls;

namespace HVO.RoofControllerV4.iPad.Converters;

/// <summary>
/// Converts a boolean value to its inverse, optionally returning <see cref="Visibility"/> states.
/// </summary>
public sealed class BoolInvertConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool flag)
        {
            return !flag;
        }

        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
