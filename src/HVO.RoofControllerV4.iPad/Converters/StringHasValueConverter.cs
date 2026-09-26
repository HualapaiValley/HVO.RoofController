using System.Globalization;
using Microsoft.Maui.Controls;

namespace HVO.RoofControllerV4.iPad.Converters;

/// <summary>
/// Returns <c>true</c> when the provided binding value is a non-empty string.
/// </summary>
public sealed class StringHasValueConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is string text && !string.IsNullOrWhiteSpace(text);
        if (Invert)
        {
            hasValue = !hasValue;
        }
        return hasValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
