using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BehavePad.Core.Analysis;

namespace BehavePad.Controls;

/// <summary>True shows the element. Pass "invert" as the parameter to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is "invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null or empty text hides the element. Pass "invert" to show it only when empty.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is not null && value is not string { Length: 0 };
        if (parameter is "invert")
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Lets a group of radio buttons bind to one value. The parameter is the enum member name or number.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string text)
        {
            return Binding.DoNothing;
        }

        return targetType.IsEnum
            ? Enum.Parse(targetType, text)
            : System.Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
    }
}

/// <summary>Shows the element when the value matches one of the comma-separated names. Start with "!," to invert.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var names = (parameter as string)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        var invert = names.Length > 0 && names[0] == "!";
        var match = names.Contains(value?.ToString());
        return match ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? $"{number * 100:0.#}%" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a severity to its status color. Pass "soft" for the translucent background version.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter is "soft";
        var key = value switch
        {
            Severity.Healthy => soft ? "AccentSoftBrush" : "AccentBrush",
            Severity.Minor => soft ? "HaloSoftBrush" : "HaloBrush",
            Severity.Moderate => soft ? "WarningSoftBrush" : "WarningBrush",
            Severity.Severe => soft ? "DriftSoftBrush" : "DriftBrush",
            _ => soft ? "SurfaceRaisedBrush" : "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Healthy => "Behaving",
        Severity.Minor => "Slight",
        Severity.Moderate => "Noticeable",
        Severity.Severe => "Severe",
        _ => "Not tested",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
