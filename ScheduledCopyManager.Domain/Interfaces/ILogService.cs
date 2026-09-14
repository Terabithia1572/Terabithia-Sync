using System;
using System.Collections.Generic;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO";
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
    }

    public interface ILogService
    {
        void LogInformation(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? exception = null);
        IReadOnlyList<LogEntry> GetRecentLogs(int count = 100);
        string GetLogDirectory();
        void ClearLogs();
    }
}
