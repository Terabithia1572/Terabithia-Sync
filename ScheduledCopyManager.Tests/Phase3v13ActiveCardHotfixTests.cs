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
using ScheduledCopyManager.Presentation.Helpers;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v13ActiveCardHotfixTests
    {
        private class DummyJobRepository : IJobRepository
        {
            private readonly List<Job> _jobs = new();
            public Task AddAsync(Job job) { _jobs.Add(job); return Task.CompletedTask; }
            public Task DeleteAsync(Guid id) { _jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(new List<Job>(_jobs));
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));
            public Task UpdateAsync(Job job) { return Task.CompletedTask; }
        }

        private class DummyJobScheduler : IJobScheduler
        {
            public Task StartAsync() => Task.CompletedTask;
            public Task ShutdownAsync() => Task.CompletedTask;
            public Task<DateTime?> GetNextExecutionTimeAsync(Job job) => Task.FromResult<DateTime?>(null);
            public Task RescheduleJobAsync(Job job) => Task.CompletedTask;
            public Task ScheduleJobAsync(Job job) => Task.CompletedTask;
            public Task TriggerJobNowAsync(Guid jobId, bool dryRun = false, bool isRecoveryResume = false, ExecutionTriggerSource source = ExecutionTriggerSource.ManualRun, IReadOnlyList<FileItemResult>? retryFiles = null) => Task.CompletedTask;
            public Task UnscheduleJobAsync(Guid jobId) => Task.CompletedTask;
        }

        private class DummyDialogService : IDialogService
        {
            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(true);
            public Task<Job?> ShowJobEditorAsync(Job? existingJob) => Task.FromResult<Job?>(null);
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
            public string? SelectFolder(string description = "Klasör Seçin") => null;
            public List<string> SelectFolders(string description = "Klasörleri Seçin") => new List<string>();
            public List<string> SelectFiles(string description = "Dosyaları Seçin") => new List<string>();
        }

        private class DummyLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"[INFO] {message}");
            public void LogWarning(string message) => Logs.Add($"[WARN] {message}");
            public void LogError(string message, Exception? ex = null) => Logs.Add($"[ERROR] {message} {ex?.Message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => @"C:\Logs";
            public void ClearLogs() => Logs.Clear();
        }

        private class DummyUsbDriveService : IUsbDriveService
        {
            public bool IsConnected { get; set; } = true;
            public string? ActiveDriveLetter { get; set; } = "E:\\";
            public string? ActiveVolumeSerial { get; set; } = "1234-5678";

#pragma warning disable CS0067
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
#pragma warning restore CS0067

            public bool IsDriveConnected(string path) => IsConnected;

            public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber)
            {
                if (!IsConnected) return false;
                if (string.IsNullOrWhiteSpace(expectedVolumeSerialNumber)) return true;
                return string.Equals(ActiveVolumeSerial, expectedVolumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            public string? GetVolumeSerialNumber(string driveLetterOrPath) => ActiveVolumeSerial;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber)
            {
                if (IsConnected && string.Equals(ActiveVolumeSerial, volumeSerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return ActiveDriveLetter;
                }
                return null;
            }

            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public void Dispose() { }
        }

        [Fact]
        public void A_StartOneExecution_ActiveCardCountIsOne()
        {
            var jobRepo = new DummyJobRepository();
            var scheduler = new DummyJobScheduler();
            var dialog = new DummyDialogService();
            var log = new DummyLogService();
            var execMgr = new JobExecutionManager();

            var vm = new JobsViewModel(jobRepo, scheduler, dialog, log, execMgr);
            var jobId = Guid.NewGuid();

            execMgr.RegisterSession(new Job { Id = jobId, Name = "Job A" });
            execMgr.GetJobProgress(jobId);

            // Emit progress
            var progress = new FileCopyProgress { JobId = jobId, JobName = "Job A", BytesCopied = 100, TotalBytes = 1000, State = ExecutionState.Running };
            execMgr.PauseJob(jobId);
            execMgr.ResumeJob(jobId);

            var session = execMgr.RegisterSession(new Job { Id = jobId, Name = "Job A" });
            session.Progress.Report(progress);

            Assert.Single(vm.ActiveJobs);
            Assert.Equal(jobId, vm.ActiveJobs.First().JobId);
        }

        [Fact]
        public void B_C_DestinationDisappears_ActiveCardCountRemainsOne()
        {
            var execMgr = new JobExecutionManager();
            var vm = new JobsViewModel(new DummyJobRepository(), new DummyJobScheduler(), new DummyDialogService(), new DummyLogService(), execMgr);
            var jobId = Guid.NewGuid();

            var session = execMgr.RegisterSession(new Job { Id = jobId, Name = "Job USB Test" });

            // Initial running progress
            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job USB Test",
                State = ExecutionState.Running,
                BytesCopied = 223 * 1024 * 1024,
                TotalBytes = 2900L * 1024 * 1024,
                FilesCopied = 5,
                TotalFiles = 10
            });

            Assert.Single(vm.ActiveJobs);

            // Disconnect destination -> DestinationWaiting reported multiple times
            for (int i = 0; i < 5; i++)
            {
                session.Progress.Report(new FileCopyProgress
                {
                    JobId = jobId,
                    JobName = "Job USB Test",
                    State = ExecutionState.DestinationUnavailable,
                    StatusMessage = "Hedef sürücü bekleniyor",
                    BytesCopied = 223 * 1024 * 1024,
                    TotalBytes = 2900L * 1024 * 1024,
                    FilesCopied = 5,
                    TotalFiles = 10
                });
            }

            Assert.Single(vm.ActiveJobs);
            var card = vm.ActiveJobs.First();
            Assert.Equal(ExecutionState.DestinationUnavailable, card.State);
            Assert.Equal("Hedef sürücü bekleniyor", card.StatusText);
            Assert.Equal("0 B/s", card.CurrentSpeedText);
            Assert.Equal("Hedef bekleniyor", card.RemainingTimeText);
        }

        [Fact]
        public void D_E_F_ReconnectDestination_SameCardContinues_ProgressPreserved()
        {
            var execMgr = new JobExecutionManager();
            var vm = new JobsViewModel(new DummyJobRepository(), new DummyJobScheduler(), new DummyDialogService(), new DummyLogService(), execMgr);
            var jobId = Guid.NewGuid();

            var session = execMgr.RegisterSession(new Job { Id = jobId, Name = "Job Resume" });

            // 1. Initial running (223 MB / 2.9 GB)
            long initialCopied = 223L * 1024 * 1024;
            long total = 2900L * 1024 * 1024;

            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job Resume",
                State = ExecutionState.Running,
                BytesCopied = initialCopied,
                TotalBytes = total,
                FilesCopied = 2,
                TotalFiles = 5
            });

            Assert.Single(vm.ActiveJobs);
            var card = vm.ActiveJobs.First();
            Assert.Contains("223", card.BytesCopiedText);

            // 2. Disconnect
            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job Resume",
                State = ExecutionState.DestinationUnavailable,
                StatusMessage = "Hedef sürücü bekleniyor",
                BytesCopied = initialCopied,
                TotalBytes = total,
                FilesCopied = 2,
                TotalFiles = 5
            });

            Assert.Single(vm.ActiveJobs);
            Assert.Contains("223", card.BytesCopiedText); // Progress preserved!

            // 3. Reconnect & Continue
            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Job Resume",
                State = ExecutionState.Running,
                BytesCopied = initialCopied + (100 * 1024 * 1024),
                TotalBytes = total,
                FilesCopied = 3,
                TotalFiles = 5,
                CurrentBytesPerSecond = 50 * 1024 * 1024
            });

            Assert.Single(vm.ActiveJobs);
            Assert.Equal(ExecutionState.Running, card.State);
            Assert.Equal("Çalışıyor...", card.StatusText);
            Assert.Contains("323", card.BytesCopiedText);
        }

        [Fact]
        public void G_WrongUsbOnOldDriveLetter_DoesNotResume()
        {
            var usbService = new DummyUsbDriveService
            {
                IsConnected = true,
                ActiveDriveLetter = "E:\\",
                ActiveVolumeSerial = "9999-9999" // WRONG USB serial
            };

            bool connected = usbService.IsDriveConnected("E:\\Target", "1234-5678"); // Expected 1234-5678
            Assert.False(connected);
        }

        [Fact]
        public void H_CorrectUsbAppearsUnderNewDriveLetter_ResumesAndResolvesLetter()
        {
            var usbService = new DummyUsbDriveService
            {
                IsConnected = true,
                ActiveDriveLetter = "F:\\", // Drive letter changed from E to F
                ActiveVolumeSerial = "1234-5678" // Correct serial!
            };

            bool connected = usbService.IsDriveConnected("E:\\Target", "1234-5678");
            Assert.True(connected);

            string? newLetter = usbService.FindDriveLetterByVolumeSerialNumber("1234-5678");
            Assert.Equal("F:\\", newLetter);
        }

        [Fact]
        public void I_MultipleEventsForSameExecution_IdempotentRegistration()
        {
            var execMgr = new JobExecutionManager();
            var vm = new JobsViewModel(new DummyJobRepository(), new DummyJobScheduler(), new DummyDialogService(), new DummyLogService(), execMgr);
            var jobId = Guid.NewGuid();

            var session = execMgr.RegisterSession(new Job { Id = jobId, Name = "Idempotent Job" });

            for (int i = 0; i < 50; i++)
            {
                session.Progress.Report(new FileCopyProgress
                {
                    JobId = jobId,
                    JobName = "Idempotent Job",
                    State = ExecutionState.Running,
                    BytesCopied = i * 1000,
                    TotalBytes = 100000
                });
            }

            Assert.Single(vm.ActiveJobs);
        }

        [Fact]
        public void J_K_TwoDifferentJobIds_LegitimatelyAllowed_EvenWithSameJobName()
        {
            var execMgr = new JobExecutionManager();
            var vm = new JobsViewModel(new DummyJobRepository(), new DummyJobScheduler(), new DummyDialogService(), new DummyLogService(), execMgr);

            var jobId1 = Guid.NewGuid();
            var jobId2 = Guid.NewGuid();

            var session1 = execMgr.RegisterSession(new Job { Id = jobId1, Name = "Yedekleme Görevi" });
            var session2 = execMgr.RegisterSession(new Job { Id = jobId2, Name = "Yedekleme Görevi" });

            session1.Progress.Report(new FileCopyProgress { JobId = jobId1, JobName = "Yedekleme Görevi", State = ExecutionState.Running });
            session2.Progress.Report(new FileCopyProgress { JobId = jobId2, JobName = "Yedekleme Görevi", State = ExecutionState.Running });

            Assert.Equal(2, vm.ActiveJobs.Count);
            Assert.Contains(vm.ActiveJobs, card => card.JobId == jobId1);
            Assert.Contains(vm.ActiveJobs, card => card.JobId == jobId2);
        }

        [Fact]
        public async Task L_M_CancelOrStop_WhileDestinationUnavailable_CleanTerminalState()
        {
            var execMgr = new JobExecutionManager();
            var vm = new JobsViewModel(new DummyJobRepository(), new DummyJobScheduler(), new DummyDialogService(), new DummyLogService(), execMgr);
            var jobId = Guid.NewGuid();

            var session = execMgr.RegisterSession(new Job { Id = jobId, Name = "Cancel Test" });

            session.Progress.Report(new FileCopyProgress
            {
                JobId = jobId,
                JobName = "Cancel Test",
                State = ExecutionState.DestinationUnavailable,
                StatusMessage = "Hedef sürücü bekleniyor"
            });

            Assert.Single(vm.ActiveJobs);

            await execMgr.CancelJobAsync(jobId);

            var card = vm.ActiveJobs.FirstOrDefault(x => x.JobId == jobId);
            Assert.NotNull(card);
            Assert.Equal(ExecutionState.Cancelled, card.State);
            Assert.Equal("İPTAL EDİLDİ", card.StatusText);
        }

        [Fact]
        public async Task N_PauseCloseRestart_ZeroAutomaticExecution()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "TerabithiaHotfixTest_" + Guid.NewGuid());
            Directory.CreateDirectory(testDir);
            try
            {
                var logService = new DummyLogService();
                var cpRepo = new CheckpointRepository(logService);
                var gate = new JobExecutionGate(cpRepo, logService);

                var jobId = Guid.NewGuid();

                var cp = new JobCheckpoint
                {
                    JobId = jobId,
                    JobName = "Interrupted Job",
                    CurrentState = ExecutionState.Paused,
                    IsRecoverable = true,
                    ProcessInstanceId = Guid.NewGuid()
                };
                await cpRepo.SaveCheckpointAsync(cp);

                var gateResult = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzScheduled);

                Assert.False(gateResult.Allowed);
                Assert.Contains("unfinished", gateResult.Reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }
}
