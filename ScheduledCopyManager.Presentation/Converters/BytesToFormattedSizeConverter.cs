using System;
using System.Globalization;
using System.Windows.Data;

namespace ScheduledCopyManager.Presentation.Converters
{
    public class BytesToFormattedSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                string[] suf = { "B", "KB", "MB", "GB", "TB" };
                if (bytes == 0)
                    return "0 B";

                long bytesAbs = Math.Abs(bytes);
                int place = System.Convert.ToInt32(Math.Floor(Math.Log(bytesAbs, 1024)));
                double num = Math.Round(bytesAbs / Math.Pow(1024, place), 1);
                return $"{(Math.Sign(bytes) * num):0.#} {suf[place]}";
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
