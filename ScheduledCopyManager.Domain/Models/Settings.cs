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
        public bool NotifyOnSuccess { get; set; } = true;
        public bool NotifyOnFailure { get; set; } = true;
        public bool NotifyOnDestinationUnavailable { get; set; } = true;
        public bool NotifyOnRecoveryWaiting { get; set; } = true;
        public int DefaultRetryCount { get; set; } = 3;
        public int DefaultRetryDelaySeconds { get; set; } = 5;
        public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Overwrite;
        public int MaxConcurrentJobs { get; set; } = 2;
        public string LogDirectory { get; set; } = string.Empty;
        public string DataDirectory { get; set; } = string.Empty;
        public bool IsFirstRun { get; set; } = true;
        public string Language { get; set; } = "tr-TR";
        public VerificationMode DefaultVerificationMode { get; set; } = VerificationMode.SizeAndTimestamp;
        public RetryPolicy DefaultRetryPolicy { get; set; } = new();
        public BandwidthLimit DefaultBandwidthLimit { get; set; } = new();
        public int CopyBufferSize { get; set; } = 4 * 1024 * 1024;

        public int GetValidatedCopyBufferSize()
        {
            return CopyBufferSize switch
            {
                524288 => 524288,   // 512 KiB
                1048576 => 1048576, // 1 MiB
                2097152 => 2097152, // 2 MiB
                4194304 => 4194304, // 4 MiB
                _ => 4194304       // Safe fallback to default 4 MiB
            };
        }
    }
}
