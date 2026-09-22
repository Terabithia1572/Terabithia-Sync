using System;
using System.Collections.Generic;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class JobCheckpoint
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid JobId { get; set; }
        public string JobName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? ScheduledStartTime { get; set; }
        public DateTime? ActualStartTime { get; set; }
        public List<string> SourcePaths { get; set; } = new();
        public string DestinationPath { get; set; } = string.Empty;
        public CopyMode CopyMode { get; set; } = CopyMode.Incremental;
        public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Overwrite;
        public VerificationMode VerificationMode { get; set; } = VerificationMode.None;
        public ExecutionState CurrentState { get; set; } = ExecutionState.Running;
        public string StatusMessage { get; set; } = string.Empty;
        public string? CurrentFile { get; set; }
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int FailedFiles { get; set; }
        public int PendingFiles { get; set; }
        public long TotalBytes { get; set; }
        public long CompletedBytes { get; set; }
        public long CurrentFileBytesCopied { get; set; }
        public long CurrentFileTotalBytes { get; set; }
        public string? LastError { get; set; }
        public string? InterruptionReason { get; set; }
        public ExecutionInterruptionReason InterruptionReasonCode { get; set; } = ExecutionInterruptionReason.None;
        public Guid? ProcessInstanceId { get; set; }
        public bool DestinationWasUnavailable { get; set; }
        public bool IsRecoverable { get; set; } = true;
        public string ApplicationVersion { get; set; } = "1.0.0";

        public List<CheckpointFileEntry> FileEntries { get; set; } = new();

        public ExecutionInterruptionReason GetEffectiveInterruptionReason()
        {
            if (InterruptionReasonCode != ExecutionInterruptionReason.None)
                return InterruptionReasonCode;

            return CurrentState switch
            {
                ExecutionState.Stopped => ExecutionInterruptionReason.UserStopped,
                ExecutionState.Paused => ExecutionInterruptionReason.UserPaused,
                ExecutionState.Cancelled => ExecutionInterruptionReason.UserCancelled,
                ExecutionState.DestinationUnavailable => ExecutionInterruptionReason.DestinationUnavailable,
                ExecutionState.Running => ExecutionInterruptionReason.UnexpectedProcessExit,
                ExecutionState.Failed => ExecutionInterruptionReason.Failure,
                _ => ExecutionInterruptionReason.None
            };
        }
    }
}
