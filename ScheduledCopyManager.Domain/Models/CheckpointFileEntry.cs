using System;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class CheckpointFileEntry
    {
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long SourceLength { get; set; }
        public DateTime SourceLastWriteTimeUtc { get; set; }
        public CheckpointFileStatus Status { get; set; } = CheckpointFileStatus.Pending;
        public long BytesCopied { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? FileHash { get; set; }
    }
}
