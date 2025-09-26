using System;
using System.Globalization;
using System.Windows.Data;

namespace NodeLinkUI
{
    public class BooleanToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? "Online" : "Offline";
            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
                return s.Equals("Online", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }
}
