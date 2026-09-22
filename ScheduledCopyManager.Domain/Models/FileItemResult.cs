using System;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class FileItemResult
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public long BytesTransferred { get; set; }
        public FileItemStatus Status { get; set; } = FileItemStatus.Pending;
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
    }
}
