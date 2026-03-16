using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SynchrolightAPI.Wpf.Converters;

public class RgbToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 3 && values[0] is byte r && values[1] is byte g && values[2] is byte b)
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        return new SolidColorBrush(Colors.Black);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
