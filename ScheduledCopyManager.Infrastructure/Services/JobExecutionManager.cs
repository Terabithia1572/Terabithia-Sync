using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class JobExecutionManager : IJobExecutionManager
    {
        private readonly ConcurrentDictionary<Guid, ActiveJobSessionImpl> _activeSessions = new();
        private readonly ICheckpointRepository? _checkpointRepository;

        public event Action<FileCopyProgress>? ProgressUpdated;
        event Action<Guid, ExecutionState>? IJobExecutionManager.JobStateChanged
        {
            add => JobStateChanged += value;
            remove => JobStateChanged -= value;
        }
        public event Action<Guid, ExecutionState>? JobStateChanged;
        public event Action<Guid>? JobCompleted;

        public JobExecutionManager(ICheckpointRepository? checkpointRepository = null)
        {
            _checkpointRepository = checkpointRepository;
        }

        public IActiveJobSession RegisterSession(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var session = new ActiveJobSessionImpl(job, OnSessionProgress, OnSessionDispose);

            if (!_activeSessions.TryAdd(job.Id, session))
            {
                if (_activeSessions.TryRemove(job.Id, out var oldSession))
                {
                    oldSession.DisposeInternal();
                }
                _activeSessions[job.Id] = session;
            }

            JobStateChanged?.Invoke(job.Id, ExecutionState.Running);
            return session;
        }

        public bool IsJobActive(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                return !session.CancellationTokenSource.IsCancellationRequested &&
                       session.CurrentProgress.State != ExecutionState.Completed &&
                       session.CurrentProgress.State != ExecutionState.Failed &&
                       session.CurrentProgress.State != ExecutionState.Cancelled &&
                       session.CurrentProgress.State != ExecutionState.Stopped;
            }
            return false;
        }

        public bool IsJobRunning(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                return IsJobActive(jobId) && session.CurrentProgress.State == ExecutionState.Running;
            }
            return false;
        }

        public bool IsJobPaused(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                return IsJobActive(jobId) && session.CurrentProgress.State == ExecutionState.Paused;
            }
            return false;
        }

        public bool IsJobStoppedByUser(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                return session.IsStoppedByUser;
            }
            return false;
        }

        public bool PauseJob(Guid jobId)
        {
            return PauseJobAsync(jobId).GetAwaiter().GetResult();
        }

        public async Task<bool> PauseJobAsync(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                session.PauseTokenSource.Pause();
                session.CurrentProgress.State = ExecutionState.Paused;
                session.CurrentProgress.StatusMessage = "DURAKLATILDI";

                if (_checkpointRepository != null)
                {
                    try
                    {
                        var cp = await _checkpointRepository.GetCheckpointAsync(jobId);
                        if (cp != null)
                        {
                            cp.CurrentState = ExecutionState.Paused;
                            cp.InterruptionReasonCode = ExecutionInterruptionReason.UserPaused;
                            cp.InterruptionReason = "Kullanıcı tarafından duraklatıldı.";
                            cp.ProcessInstanceId = JobExecutionGate.CurrentProcessInstanceId;
                            cp.UpdatedAt = DateTime.Now;
                            await _checkpointRepository.SaveCheckpointAsync(cp);
                        }
                    }
                    catch { }
                }

                JobStateChanged?.Invoke(jobId, ExecutionState.Paused);
                ProgressUpdated?.Invoke(session.CurrentProgress);
                return true;
            }
            return false;
        }

        public bool ResumeJob(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                session.PauseTokenSource.Resume();
                session.CurrentProgress.State = ExecutionState.Running;
                session.CurrentProgress.StatusMessage = "Kopyalanıyor...";
                JobStateChanged?.Invoke(jobId, ExecutionState.Running);
                ProgressUpdated?.Invoke(session.CurrentProgress);
                return true;
            }
            return false;
        }

        public bool StopJob(Guid jobId)
        {
            return StopJobAsync(jobId).GetAwaiter().GetResult();
        }

        public async Task<bool> StopJobAsync(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                session.IsStoppedByUser = true;
                session.CurrentProgress.State = ExecutionState.Stopped;
                session.CurrentProgress.StatusMessage = "DURDURULDU";

                if (_checkpointRepository != null)
                {
                    try
                    {
                        var cp = await _checkpointRepository.GetCheckpointAsync(jobId);
                        if (cp != null)
                        {
                            cp.CurrentState = ExecutionState.Stopped;
                            cp.InterruptionReasonCode = ExecutionInterruptionReason.UserStopped;
                            cp.InterruptionReason = "Kullanıcı tarafından durduruldu.";
                            cp.UpdatedAt = DateTime.Now;
                            await _checkpointRepository.SaveCheckpointAsync(cp);
                        }
                    }
                    catch { }
                }

                session.CancellationTokenSource.Cancel();
                JobStateChanged?.Invoke(jobId, ExecutionState.Stopped);
                ProgressUpdated?.Invoke(session.CurrentProgress);
                return true;
            }
            return false;
        }

        public bool CancelJob(Guid jobId)
        {
            return CancelJobAsync(jobId).GetAwaiter().GetResult();
        }

        public async Task<bool> CancelJobAsync(Guid jobId)
        {
            if (_activeSessions.TryGetValue(jobId, out var session))
            {
                session.CurrentProgress.State = ExecutionState.Cancelled;
                session.CurrentProgress.StatusMessage = "İPTAL EDİLDİ";

                if (_checkpointRepository != null)
                {
                    try
                    {
                        var cp = await _checkpointRepository.GetCheckpointAsync(jobId);
                        if (cp != null)
                        {
                            cp.CurrentState = ExecutionState.Cancelled;
                            cp.InterruptionReasonCode = ExecutionInterruptionReason.UserCancelled;
                            cp.InterruptionReason = "Kullanıcı tarafından iptal edildi.";
                            cp.UpdatedAt = DateTime.Now;
                            await _checkpointRepository.SaveCheckpointAsync(cp);
                        }
                    }
                    catch { }
                }

                session.CancellationTokenSource.Cancel();
                JobStateChanged?.Invoke(jobId, ExecutionState.Cancelled);
                ProgressUpdated?.Invoke(session.CurrentProgress);
                return true;
            }
            return false;
        }

        public IReadOnlyCollection<FileCopyProgress> GetActiveJobProgresses()
        {
            return _activeSessions.Values.Select(s => s.CurrentProgress).ToList().AsReadOnly();
        }

        public FileCopyProgress? GetJobProgress(Guid jobId)
        {
            return _activeSessions.TryGetValue(jobId, out var session) ? session.CurrentProgress : null;
        }

        private void OnSessionProgress(FileCopyProgress progress)
        {
            ProgressUpdated?.Invoke(progress);
        }

        private void OnSessionDispose(ActiveJobSessionImpl session)
        {
            _activeSessions.TryRemove(session.JobId, out _);
            JobCompleted?.Invoke(session.JobId);
        }

        private class ActiveJobSessionImpl : IActiveJobSession, IProgress<FileCopyProgress>
        {
            private readonly Action<FileCopyProgress> _onProgress;
            private readonly Action<ActiveJobSessionImpl> _onDispose;
            private bool _isDisposed;

            public Guid JobId { get; }
            public string JobName { get; }
            public PauseTokenSource PauseTokenSource { get; } = new();
            public CancellationTokenSource CancellationTokenSource { get; } = new();
            public IProgress<FileCopyProgress> Progress => this;
            public FileCopyProgress CurrentProgress { get; private set; }
            public bool IsStoppedByUser { get; set; }

            public ActiveJobSessionImpl(Job job, Action<FileCopyProgress> onProgress, Action<ActiveJobSessionImpl> onDispose)
            {
                JobId = job.Id;
                JobName = job.Name ?? string.Empty;
                _onProgress = onProgress;
                _onDispose = onDispose;

                CurrentProgress = new FileCopyProgress
                {
                    JobId = job.Id,
                    JobName = JobName,
                    State = ExecutionState.Running,
                    StatusMessage = "Başlatılıyor..."
                };
            }

            public void Report(FileCopyProgress value)
            {
                if (value != null)
                {
                    CurrentProgress = value;
                    _onProgress(value);
                }
            }

            public void Dispose()
            {
                DisposeInternal();
            }

            public void DisposeInternal()
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                    _onDispose(this);
                    CancellationTokenSource.Dispose();
                }
            }
        }
    }
}
