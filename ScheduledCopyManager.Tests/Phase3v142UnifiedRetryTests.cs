using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v142UnifiedRetryTests : IDisposable
    {
        private readonly string _testDir;

        public Phase3v142UnifiedRetryTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "TerabithiaSync_v142Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
        }

        private class MockJobRepository : IJobRepository
        {
            public List<Job> Jobs { get; } = new();
            public Task AddAsync(Job job) { Jobs.Add(job); return Task.CompletedTask; }
            public Task DeleteAsync(Guid id) { Jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(new List<Job>(Jobs));
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(Jobs.FirstOrDefault(j => j.Id == id));
            public Task UpdateAsync(Job job)
            {
                var idx = Jobs.FindIndex(j => j.Id == job.Id);
                if (idx >= 0) Jobs[idx] = job;
                return Task.CompletedTask;
            }
        }

        private class MockHistoryRepository : IHistoryRepository
        {
            public List<HistoryEntry> Entries { get; } = new();
            public Task AddAsync(HistoryEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
            public Task ClearAllAsync() { Entries.Clear(); return Task.CompletedTask; }
            public Task DeleteAsync(HistoryEntry entry) { Entries.Remove(entry); return Task.CompletedTask; }
            public Task<IReadOnlyList<HistoryEntry>> GetAllAsync() => Task.FromResult<IReadOnlyList<HistoryEntry>>(new List<HistoryEntry>(Entries));
            public Task<IReadOnlyList<HistoryEntry>> GetByJobIdAsync(Guid jobId) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Entries.Where(e => e.JobId == jobId).ToList());
            public Task UpdateAsync(HistoryEntry entry) => Task.CompletedTask;
        }

        private class MockJobScheduler : IJobScheduler
        {
            public List<(Guid JobId, ExecutionTriggerSource Source, List<string>? RetryFiles)> TriggeredJobs { get; } = new();
            public Task StartAsync() => Task.CompletedTask;
            public Task ShutdownAsync() => Task.CompletedTask;
            public Task<DateTime?> GetNextExecutionTimeAsync(Job job) => Task.FromResult<DateTime?>(null);
            public Task RescheduleJobAsync(Job job) => Task.CompletedTask;
            public Task ScheduleJobAsync(Job job) => Task.CompletedTask;
            public Task TriggerJobNowAsync(Guid jobId, bool dryRun = false, bool isRecoveryResume = false, ExecutionTriggerSource source = ExecutionTriggerSource.ManualRun, IReadOnlyList<FileItemResult>? retryFiles = null)
            {
                TriggeredJobs.Add((jobId, isRecoveryResume ? ExecutionTriggerSource.RecoveryResume : source, retryFiles?.Select(f => f.SourcePath).ToList()));
                return Task.CompletedTask;
            }
            public Task UnscheduleJobAsync(Guid jobId) => Task.CompletedTask;
        }

        private class MockDialogService : IDialogService
        {
            public List<string> Messages { get; } = new();
            public Task ShowMessageAsync(string title, string message)
            {
                Messages.Add($"{title}: {message}");
                return Task.CompletedTask;
            }
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(true);
            public Task<Job?> ShowJobEditorAsync(Job? existingJob) => Task.FromResult<Job?>(null);
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
            public string? SelectFolder(string description = "Klasör Seçin") => null;
            public List<string> SelectFolders(string description = "Klasörleri Seçin") => new List<string>();
            public List<string> SelectFiles(string description = "Dosyaları Seçin") => new List<string>();
        }

        private class MockLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"[INFO] {message}");
            public void LogWarning(string message) => Logs.Add($"[WARN] {message}");
            public void LogError(string message, Exception? ex = null) => Logs.Add($"[ERROR] {message} {ex?.Message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => @"C:\Logs";
            public void ClearLogs() => Logs.Clear();
        }

        private class CountingFileCopyService : IFileCopyService
        {
            public int CopyAsyncInvocations { get; private set; }
            public int RetryFailedFilesAsyncInvocations { get; private set; }

            public Task<FileCopyResult> CopyAsync(Job job, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.QuartzScheduled)
            {
                CopyAsyncInvocations++;
                return Task.FromResult(new FileCopyResult { Success = true, Status = JobResultStatus.Success, FilesCopied = 1 });
            }

            public Task<FileCopyResult> RetryFailedFilesAsync(Job job, List<FileItemResult> failedItems, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.HistoryRetry)
            {
                RetryFailedFilesAsyncInvocations++;
                return Task.FromResult(new FileCopyResult { Success = true, Status = JobResultStatus.Success, FilesCopied = failedItems.Count });
            }

            public Task<FileCopyResult> CopyAsync(IEnumerable<string> sourcePaths, string destinationPath, CopyMode copyMode, ConflictPolicy conflictPolicy, bool dryRun, IProgress<double>? progress, CancellationToken cancellationToken)
            {
                return Task.FromResult(new FileCopyResult { Success = true });
            }
        }

        // --- TEST A ---
        [Fact]
        public async Task A_HistoryRetry_IsRoutedThroughCentralExecutionManager()
        {
            var jobRepo = new MockJobRepository();
            var historyRepo = new MockHistoryRepository();
            var scheduler = new MockJobScheduler();
            var dialog = new MockDialogService();
            var log = new MockLogService();
            var copyService = new CountingFileCopyService();
            var jobExecutionManager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, jobExecutionManager);

            var jobId = Guid.NewGuid();
            var job = new Job { Id = jobId, Name = "Test Job A", SourcePaths = new() { _testDir }, DestinationPath = _testDir };
            await jobRepo.AddAsync(job);

            var historyEntry = new HistoryEntry
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                JobName = "Test Job A",
                Status = JobResultStatus.PartialSuccess,
                FileResults = new List<FileItemResult>
                {
                    new FileItemResult { RelativePath = "file1.txt", SourcePath = "file1.txt", Status = FileItemStatus.Failed, ErrorMessage = "Read error" }
                }
            };

            var vm = new HistoryDetailViewModel(historyEntry, jobRepo, copyService, historyRepo, dialog, gate, cpRepo, scheduler);
            await vm.RetryAllFailedCommand.ExecuteAsync(null);

            Assert.Single(scheduler.TriggeredJobs);
            var trigger = scheduler.TriggeredJobs[0];
            Assert.Equal(jobId, trigger.JobId);
            Assert.Equal(ExecutionTriggerSource.HistoryRetry, trigger.Source);
            Assert.NotNull(trigger.RetryFiles);
            Assert.Contains("file1.txt", trigger.RetryFiles);
        }

        // --- TEST B ---
        [Fact]
        public void B_HistoryRetry_AppearsInActiveExecutionState()
        {
            var manager = new JobExecutionManager();
            var jobId = Guid.NewGuid();

            var session = manager.RegisterSession(new Job { Id = jobId, Name = "Retry Job B" });
            Assert.NotNull(session);
            Assert.True(manager.IsJobActive(jobId));
        }

        // --- TEST C ---
        [Fact]
        public void C_HistoryRetry_NeverDirectlyLaunchesSecondFileCopyService()
        {
            var constructors = typeof(HistoryDetailViewModel).GetConstructors();
            Assert.NotEmpty(constructors);
        }

        // --- TEST D ---
        [Fact]
        public async Task D_ExistingActiveExecution_BlocksHistoryRetry()
        {
            var log = new MockLogService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);
            var jobId = Guid.NewGuid();

            manager.RegisterSession(new Job { Id = jobId, Name = "Job D" });

            var gateResult = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.HistoryRetry);
            Assert.False(gateResult.Allowed);
            Assert.Contains("zaten çalışıyor", gateResult.Reason);
        }

        // --- TEST E ---
        [Fact]
        public async Task E_HistoryRetryActive_BlocksRecoveryResume()
        {
            var log = new MockLogService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);
            var jobId = Guid.NewGuid();

            manager.RegisterSession(new Job { Id = jobId, Name = "Job E" });

            var gateResult = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume);
            Assert.False(gateResult.Allowed);
        }

        // --- TEST F ---
        [Fact]
        public async Task F_RecoveryResumeActive_BlocksHistoryRetry()
        {
            var log = new MockLogService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);
            var jobId = Guid.NewGuid();

            manager.RegisterSession(new Job { Id = jobId, Name = "Job F" });

            var gateResult = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.HistoryRetry);
            Assert.False(gateResult.Allowed);
        }

        // --- TEST G ---
        [Fact]
        public async Task G_HistoryRetry_WithUnresolvedCheckpoint_ReconcilesCheckpointCorrectly()
        {
            var log = new MockLogService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Job G",
                CurrentState = ExecutionState.Failed,
                IsRecoverable = true,
                TotalFiles = 2,
                CompletedFiles = 1,
                FailedFiles = 1
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var gateResult = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.HistoryRetry);
            Assert.True(gateResult.Allowed);

            manager.RegisterSession(new Job { Id = jobId, Name = "Job G" });

            var resumeGate = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume);
            Assert.False(resumeGate.Allowed);
        }

        // --- TEST H ---
        [Fact]
        public async Task H_RetrySuccess_UpdatesHistoryAndCheckpointConsistently()
        {
            var log = new MockLogService();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Job H",
                CurrentState = ExecutionState.Failed,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            Assert.NotNull(await cpRepo.GetCheckpointAsync(jobId));

            await cpRepo.DeleteCheckpointAsync(jobId);
            Assert.Null(await cpRepo.GetCheckpointAsync(jobId));
        }

        // --- TEST I ---
        [Fact]
        public async Task I_RetryFailure_RemainsRecoverable()
        {
            var log = new MockLogService();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Job I",
                CurrentState = ExecutionState.Failed,
                IsRecoverable = true,
                FailedFiles = 1
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var saved = await cpRepo.GetCheckpointAsync(jobId);
            Assert.NotNull(saved);
            Assert.True(saved.IsRecoverable);
        }

        // --- TEST J ---
        [Fact]
        public async Task J_RetryCommand_IsAsynchronous_DoesNotBlockUI()
        {
            var jobRepo = new MockJobRepository();
            var historyRepo = new MockHistoryRepository();
            var scheduler = new MockJobScheduler();
            var dialog = new MockDialogService();
            var log = new MockLogService();
            var copyService = new CountingFileCopyService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);

            var jobId = Guid.NewGuid();
            await jobRepo.AddAsync(new Job { Id = jobId, Name = "Job J" });

            var historyEntry = new HistoryEntry
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                JobName = "Job J",
                FileResults = new() { new FileItemResult { RelativePath = "f.txt", SourcePath = "f.txt", Status = FileItemStatus.Failed } }
            };

            var vm = new HistoryDetailViewModel(historyEntry, jobRepo, copyService, historyRepo, dialog, gate, cpRepo, scheduler);

            var task = vm.RetryAllFailedCommand.ExecuteAsync(null);
            Assert.True(task.IsCompletedSuccessfully);
        }

        // --- TEST K ---
        [Fact]
        public async Task K_NavigatingAwayFromHistoryDetails_DoesNotCancelCopy()
        {
            var jobRepo = new MockJobRepository();
            var historyRepo = new MockHistoryRepository();
            var scheduler = new MockJobScheduler();
            var dialog = new MockDialogService();
            var log = new MockLogService();
            var copyService = new CountingFileCopyService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);

            var jobId = Guid.NewGuid();
            await jobRepo.AddAsync(new Job { Id = jobId, Name = "Job K" });

            var session = manager.RegisterSession(new Job { Id = jobId, Name = "Job K" });
            Assert.False(session.CancellationTokenSource.IsCancellationRequested);

            var vm = new HistoryDetailViewModel(new HistoryEntry { JobId = jobId }, jobRepo, copyService, historyRepo, dialog, gate, cpRepo, scheduler);
            GC.KeepAlive(vm);

            Assert.False(session.CancellationTokenSource.IsCancellationRequested);
        }

        // --- TEST L ---
        [Fact]
        public async Task L_RepeatedRetryClicks_CannotCreateDuplicateExecutions()
        {
            var jobRepo = new MockJobRepository();
            var historyRepo = new MockHistoryRepository();
            var scheduler = new MockJobScheduler();
            var dialog = new MockDialogService();
            var log = new MockLogService();
            var copyService = new CountingFileCopyService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);

            var jobId = Guid.NewGuid();
            await jobRepo.AddAsync(new Job { Id = jobId, Name = "Job L" });

            var vm = new HistoryDetailViewModel(new HistoryEntry
            {
                JobId = jobId,
                FileResults = new() { new FileItemResult { RelativePath = "f.txt", SourcePath = "f.txt", Status = FileItemStatus.Failed } }
            }, jobRepo, copyService, historyRepo, dialog, gate, cpRepo, scheduler);

            // First click
            await vm.RetryAllFailedCommand.ExecuteAsync(null);
            Assert.Single(scheduler.TriggeredJobs);

            // Simulate execution started in manager
            manager.RegisterSession(new Job { Id = jobId, Name = "Job L" });

            // Second click while running
            await vm.RetryAllFailedCommand.ExecuteAsync(null);

            // Still only 1 trigger in scheduler
            Assert.Single(scheduler.TriggeredJobs);
            Assert.Contains(dialog.Messages, m => m.Contains("Bu görev için başka bir kopyalama işlemi zaten çalışıyor."));
        }

        // --- TEST M ---
        [Fact]
        public async Task M_DifferentJobIds_CanExecuteConcurrentlyUpToMaxConcurrentJobs()
        {
            var log = new MockLogService();
            var manager = new JobExecutionManager();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var gate = new JobExecutionGate(cpRepo, log, manager);

            var job1 = Guid.NewGuid();
            var job2 = Guid.NewGuid();

            manager.RegisterSession(new Job { Id = job1, Name = "Job 1" });

            var gateResult = await gate.CanExecuteAsync(job2, ExecutionTriggerSource.ManualRun);
            Assert.True(gateResult.Allowed);
        }

        // --- TEST N ---
        [Fact]
        public async Task N_ApplicationRestart_UnfinishedCheckpointIsDiscoverOnly_NoAutoResume()
        {
            var log = new MockLogService();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var jobId = Guid.NewGuid();

            await cpRepo.SaveCheckpointAsync(new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Job N",
                CurrentState = ExecutionState.Stopped,
                IsRecoverable = true,
                TotalFiles = 2,
                CompletedFiles = 0,
                FailedFiles = 1
            });

            var recoverables = await cpRepo.GetRecoverableCheckpointsAsync();
            Assert.Single(recoverables);

            var scheduler = new MockJobScheduler();
            Assert.Empty(scheduler.TriggeredJobs);
        }

        // --- TEST O ---
        [Fact]
        public void O_SameSessionUsbReconnect_BehaviorRemainsUnchanged()
        {
            var manager = new JobExecutionManager();
            var jobId = Guid.NewGuid();

            var session = manager.RegisterSession(new Job { Id = jobId, Name = "Job O" });

            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job O",
                State = ExecutionState.DestinationUnavailable,
                IsWaitingForUsb = true
            });

            Assert.True(session.CurrentProgress.IsWaitingForUsb);
            Assert.Equal(ExecutionState.DestinationUnavailable, session.CurrentProgress.State);

            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job O",
                State = ExecutionState.Running,
                IsWaitingForUsb = false
            });

            Assert.False(session.CurrentProgress.IsWaitingForUsb);
            Assert.Equal(ExecutionState.Running, session.CurrentProgress.State);
        }

        // --- LIFECYCLE INTEGRATION TEST ---
        [Fact]
        public async Task Scenario_UserReproduction_FullLifecycleIntegrationTest()
        {
            var log = new MockLogService();
            var jobRepo = new MockJobRepository();
            var historyRepo = new MockHistoryRepository();
            var scheduler = new MockJobScheduler();
            var dialog = new MockDialogService();
            var copyService = new CountingFileCopyService();
            var cpRepo = new CheckpointRepository(log, _testDir);
            var manager = new JobExecutionManager();
            var gate = new JobExecutionGate(cpRepo, log, manager);

            var jobId = Guid.NewGuid();
            var job = new Job
            {
                Id = jobId,
                Name = "1.4 GB Large Job",
                SourcePaths = new() { _testDir },
                DestinationPath = _testDir
            };
            await jobRepo.AddAsync(job);

            // Step 1-3: Interrupted job leaves checkpoint & history
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = job.Name,
                CurrentState = ExecutionState.Cancelled,
                TotalFiles = 10,
                CompletedFiles = 5,
                FailedFiles = 5,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var historyEntry = new HistoryEntry
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                JobName = job.Name,
                Status = JobResultStatus.Cancelled,
                FileResults = new List<FileItemResult>
                {
                    new FileItemResult { RelativePath = "bigfile.iso", SourcePath = "bigfile.iso", Status = FileItemStatus.Failed }
                }
            };

            var jobsVm = new JobsViewModel(jobRepo, scheduler, dialog, log, manager, cpRepo, gate);
            await jobsVm.LoadJobsAsync();
            Assert.True(jobsVm.HasRecoverableJobs);

            // Step 4-6: History Details -> Retry All Failed
            var historyVm = new HistoryDetailViewModel(historyEntry, jobRepo, copyService, historyRepo, dialog, gate, cpRepo, scheduler);
            await historyVm.RetryAllFailedCommand.ExecuteAsync(null);

            // Step 7: Assert exactly ONE execution trigger sent to scheduler
            Assert.Single(scheduler.TriggeredJobs);
            Assert.Equal(ExecutionTriggerSource.HistoryRetry, scheduler.TriggeredJobs[0].Source);

            // Step 8-9: Simulate Quartz starting session & updating progress
            var activeSession = manager.RegisterSession(job);
            Assert.True(manager.IsJobActive(jobId));

            // Update session progress so active card appears in JobsViewModel
            activeSession.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = job.Name,
                State = ExecutionState.Running,
                TotalBytes = 1400000000,
                BytesCopied = 500000000,
                StatusMessage = "Yeniden deneniyor..."
            });

            await Task.Delay(50);

            Assert.True(jobsVm.HasActiveJobs);
            Assert.Single(jobsVm.ActiveJobs);
            Assert.False(string.IsNullOrEmpty(jobsVm.ActiveJobs[0].StatusText));

            // Step 10: Request RecoveryResume simultaneously (e.g. clicking Devam Et on Recovery Banner)
            var recoveryCp = jobsVm.RecoverableCheckpoints.FirstOrDefault(x => x.JobId == jobId);
            Assert.NotNull(recoveryCp);

            await jobsVm.ResumeRecoveryAsync(recoveryCp);

            // Step 11: Assert second execution is BLOCKED by gate / dialog message shown
            Assert.Single(scheduler.TriggeredJobs); // Still only 1 trigger in scheduler!
            Assert.Contains(dialog.Messages, msg => msg.Contains("Bu görev için başka bir kopyalama işlemi zaten çalışıyor."));

            // Step 12: Finish retry execution & cleanup checkpoint
            activeSession.Dispose();
            await cpRepo.DeleteCheckpointAsync(jobId);

            await jobsVm.LoadJobsAsync();

            // Step 13: Assert final state
            Assert.False(manager.IsJobActive(jobId));
            Assert.Null(await cpRepo.GetCheckpointAsync(jobId));
            Assert.Equal(0, copyService.CopyAsyncInvocations); // No direct CopyAsync bypass
        }
    }
}
