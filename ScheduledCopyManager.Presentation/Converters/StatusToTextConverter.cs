using System;
using System.Globalization;
using System.Windows.Data;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Presentation.Converters
{
    public class StatusToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is JobResultStatus status)
            {
                return status switch
                {
                    JobResultStatus.Success => "BAŞARILI",
                    JobResultStatus.PartialSuccess => "KISMİ BAŞARILI",
                    JobResultStatus.Failure => "BAŞARISIZ",
                    JobResultStatus.Cancelled => "İPTAL EDİLDİ",
                    JobResultStatus.Skipped => "ATLANDI",
                    _ => "BİLİNMİYOR"
                };
            }
            return "BİLİNMİYOR";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
