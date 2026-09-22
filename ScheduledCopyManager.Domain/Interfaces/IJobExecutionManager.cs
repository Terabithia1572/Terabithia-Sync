using System;
using System.Collections.Generic;
using System.Threading;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IActiveJobSession : IDisposable
    {
        Guid JobId { get; }
        string JobName { get; }
        PauseTokenSource PauseTokenSource { get; }
        CancellationTokenSource CancellationTokenSource { get; }
        IProgress<FileCopyProgress> Progress { get; }
        FileCopyProgress CurrentProgress { get; }
        bool IsStoppedByUser { get; set; }
    }

    public interface IJobExecutionManager
    {
        IActiveJobSession RegisterSession(Job job);
        bool IsJobActive(Guid jobId);
        bool IsJobRunning(Guid jobId);
        bool IsJobPaused(Guid jobId);
        bool IsJobStoppedByUser(Guid jobId);
        bool PauseJob(Guid jobId);
        Task<bool> PauseJobAsync(Guid jobId);
        bool ResumeJob(Guid jobId);
        bool StopJob(Guid jobId);
        Task<bool> StopJobAsync(Guid jobId);
        bool CancelJob(Guid jobId);
        Task<bool> CancelJobAsync(Guid jobId);
        IReadOnlyCollection<FileCopyProgress> GetActiveJobProgresses();
        FileCopyProgress? GetJobProgress(Guid jobId);

        event Action<FileCopyProgress>? ProgressUpdated;
        event Action<Guid, ExecutionState>? JobStateChanged;
        event Action<Guid>? JobCompleted;
    }
}
