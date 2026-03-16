using System.Globalization;
using System.Windows.Data;

namespace SynchrolightAPI.Wpf.Converters;

public class ByteToHexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is byte b)
            return b.ToString("X2");
        return "00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return b;
        return (byte)0;
    }
}
