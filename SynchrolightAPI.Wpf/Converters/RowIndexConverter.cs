using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace SynchrolightAPI.Wpf.Converters;

public class RowIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DataGridRow row)
            return (row.GetIndex() + 1).ToString();
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
