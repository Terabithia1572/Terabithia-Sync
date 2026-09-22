using System;
using System.Collections.Generic;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class HistoryEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid JobId { get; set; }
        public string JobName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesFailed { get; set; }
        public int FilesIncomplete { get; set; }
        public long BytesCopied { get; set; }
        public long BytesWrittenThisExecution { get; set; }
        public int TotalFilesPlanned { get; set; }
        public long TotalBytesPlanned { get; set; }
        public JobResultStatus Status { get; set; } = JobResultStatus.Success;
        public string Message { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
        public List<FileItemResult> FileResults { get; set; } = new();

        // Phase 1 Prepared Extensions
        public DateTime? ScheduledStartTime { get; set; }
        public DateTime? ActualStartTime { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? TotalDuration { get; set; }
        public TimeSpan? ActiveCopyDuration { get; set; }
        public TimeSpan? PausedDuration { get; set; }
        public long? TotalBytes { get; set; }
        public long? CopiedBytes { get; set; }
        public int? TotalFiles { get; set; }
        public int? SuccessfulFiles { get; set; }
        public int? SkippedFiles { get; set; }
        public int? FailedFiles { get; set; }
        public int? RetryCount { get; set; }
        public double? AverageBytesPerSecond { get; set; }
    }
}
