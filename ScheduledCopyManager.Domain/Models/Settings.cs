using System;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class Settings
    {
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool EnableTrayIcon { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool EnableNotifications { get; set; } = true;
        public int DefaultRetryCount { get; set; } = 3;
        public int DefaultRetryDelaySeconds { get; set; } = 5;
        public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Overwrite;
        public int MaxConcurrentJobs { get; set; } = 2;
        public string LogDirectory { get; set; } = string.Empty;
        public string DataDirectory { get; set; } = string.Empty;
        public bool IsFirstRun { get; set; } = true;
    }
}
