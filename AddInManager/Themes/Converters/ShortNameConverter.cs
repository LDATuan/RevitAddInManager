using System.Windows.Data;

namespace RevitAddinManager.Themes.Converters;

/// <summary>
/// "RevitTest.Commands.HelloCommand" -> "HelloCommand". The namespace is already implied by the
/// assembly row above it, so repeating it on every command row is just noise.
/// </summary>
public class ShortNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string name) return value;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
