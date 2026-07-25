using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WorkSpaceApp;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

public class BoolInverterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b && !b;
}

public class CountLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int count = value is int i ? i : 0;
        string label = parameter?.ToString() ?? string.Empty;
        return $"{count} {label}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class HexColorToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || !hex.StartsWith('#')) return null;
        try
        {
            var trimmed = hex.TrimStart('#');
            if (trimmed.Length == 6) trimmed = "FF" + trimmed;
            uint argb = System.Convert.ToUInt32(trimmed, 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class HexColorToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && s.StartsWith('#') ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class PriorityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var (r, g, b) = value?.ToString()?.ToLower() switch
        {
            "high"   => ((byte)255, (byte)71,  (byte)87),   // #ff4757
            "medium" => ((byte)0,   (byte)103, (byte)192),  // #0067c0
            _        => ((byte)136, (byte)136, (byte)136)   // low / muted
        };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
