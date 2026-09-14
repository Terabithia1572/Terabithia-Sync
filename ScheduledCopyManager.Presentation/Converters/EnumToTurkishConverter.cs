using System;
using System.Globalization;
using System.Windows.Data;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Presentation.Converters
{
    public class EnumToTurkishConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;

            if (value is CopyMode copyMode)
            {
                return copyMode switch
                {
                    CopyMode.Incremental => "Artımlı Kopyalama",
                    CopyMode.Mirror => "Ayna Modu",
                    CopyMode.VerifyOnly => "Yalnızca Doğrula",
                    _ => copyMode.ToString()
                };
            }

            if (value is ConflictPolicy conflictPolicy)
            {
                return conflictPolicy switch
                {
                    ConflictPolicy.Skip => "Atla",
                    ConflictPolicy.Overwrite => "Üzerine Yaz",
                    ConflictPolicy.Rename => "Yeniden Adlandır",
                    _ => conflictPolicy.ToString()
                };
            }

            if (value is ScheduleType scheduleType)
            {
                return scheduleType switch
                {
                    ScheduleType.OneTime => "Tek Seferlik",
                    ScheduleType.Daily => "Her Gün",
                    ScheduleType.Weekly => "Haftalık",
                    ScheduleType.Monthly => "Aylık",
                    ScheduleType.Cron => "Gelişmiş Zamanlama",
                    _ => scheduleType.ToString()
                };
            }

            if (value is MissedJobBehavior missedBehavior)
            {
                return missedBehavior switch
                {
                    MissedJobBehavior.RunImmediately => "Kaçırılan görevi hemen çalıştır",
                    MissedJobBehavior.Skip => "Kaçırılan görevi atla",
                    MissedJobBehavior.Reschedule => "Görevi yeniden planla",
                    _ => missedBehavior.ToString()
                };
            }

            if (value is JobResultStatus resultStatus)
            {
                return resultStatus switch
                {
                    JobResultStatus.Success => "Başarılı",
                    JobResultStatus.PartialSuccess => "Kısmen Başarılı",
                    JobResultStatus.Failure => "Başarısız",
                    JobResultStatus.Cancelled => "İptal Edildi",
                    JobResultStatus.Skipped => "Atlandı",
                    _ => resultStatus.ToString()
                };
            }

            return value.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
