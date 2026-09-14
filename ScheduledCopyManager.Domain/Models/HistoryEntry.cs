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
        public long BytesCopied { get; set; }
        public JobResultStatus Status { get; set; } = JobResultStatus.Success;
        public string Message { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
        public List<FileItemResult> FileResults { get; set; } = new();
    }
}
