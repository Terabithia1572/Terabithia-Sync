using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Presentation.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is JobResultStatus status)
            {
                return status switch
                {
                    JobResultStatus.Success => new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 204, 113)),       // Green
                    JobResultStatus.PartialSuccess => new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 196, 15)), // Orange
                    JobResultStatus.Failure => new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)),       // Red
                    JobResultStatus.Cancelled => new SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 89, 182)),    // Purple
                    JobResultStatus.Skipped => new SolidColorBrush(System.Windows.Media.Color.FromRgb(149, 165, 166)),     // Gray
                    _ => new SolidColorBrush(Colors.Gray)
                };
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
