using System;
using System.Globalization;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Presentation.Helpers
{
    public static class FormattingHelpers
    {
        private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB" };

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";

            int i = 0;
            double dblBytes = bytes;

            while (dblBytes >= 1024 && i < SizeSuffixes.Length - 1)
            {
                dblBytes /= 1024;
                i++;
            }

            if (i == 0)
                return $"{bytes} B";

            return $"{dblBytes:N1} {SizeSuffixes[i]}";
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            {
                return "0 B/s";
            }

            int i = 0;
            double dblSpeed = bytesPerSecond;

            while (dblSpeed >= 1024 && i < SizeSuffixes.Length - 1)
            {
                dblSpeed /= 1024;
                i++;
            }

            if (i == 0)
                return $"{bytesPerSecond:N0} B/s";

            return $"{dblSpeed:N1} {SizeSuffixes[i]}/s";
        }

        public static string FormatRemainingTime(TimeSpan remainingTime, ExecutionState state)
        {
            if (state == ExecutionState.Paused)
            {
                return "Duraklatıldı";
            }

            if (state == ExecutionState.DestinationUnavailable || state == ExecutionState.WaitingForDrive)
            {
                return "Hedef bekleniyor";
            }

            if (remainingTime <= TimeSpan.Zero || remainingTime.TotalDays > 10)
            {
                return "Hesaplanıyor...";
            }

            if (remainingTime.TotalHours >= 1)
            {
                return $"~{(int)remainingTime.TotalHours:D2}:{remainingTime.Minutes:D2}:{remainingTime.Seconds:D2}";
            }

            return $"~{remainingTime.Minutes:D2}:{remainingTime.Seconds:D2}";
        }

        public static string TrimPath(string path, int maxLength = 45)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (path.Length <= maxLength) return path;

            string fileName = System.IO.Path.GetFileName(path);
            if (fileName.Length >= maxLength - 5)
            {
                return "..." + fileName.Substring(fileName.Length - (maxLength - 5));
            }

            int remaining = maxLength - fileName.Length - 5;
            string prefix = path.Substring(0, Math.Max(0, remaining));

            return prefix + "...\\" + fileName;
        }
    }
}
