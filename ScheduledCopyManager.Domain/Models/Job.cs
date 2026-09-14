using System;
using System.Collections.Generic;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class Job
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public List<string> SourcePaths { get; set; } = new();
        public string DestinationPath { get; set; } = string.Empty;
        public JobSchedule Schedule { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public CopyMode CopyMode { get; set; } = CopyMode.Incremental;
        public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Overwrite;
        public MissedJobBehavior MissedJobBehavior { get; set; } = MissedJobBehavior.RunImmediately;
        public bool VerifyCopy { get; set; } = false;
        public bool PreserveTimestamps { get; set; } = true;
        public bool PreserveAttributes { get; set; } = true;
        public bool CreateDestinationIfMissing { get; set; } = true;
        public bool ContinueOnError { get; set; } = true;
        public bool EnableMirrorDeletion { get; set; } = false;
        public bool IsUsbDestination { get; set; } = false;
        public int RetryCount { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
        public DateTime? LastRun { get; set; }
        public JobResultStatus? LastResult { get; set; }
        public DateTime? NextRun { get; set; }
    }
}
