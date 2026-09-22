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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3RecoverySafetyRegressionTests
    {
        private readonly string _testBaseDir;

        public Phase3RecoverySafetyRegressionTests()
        {
            _testBaseDir = Path.Combine(Path.GetTempPath(), "Phase3SafetyTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testBaseDir);
        }

        private class DummyJobExecutionContext : Quartz.IJobExecutionContext
        {
            public Quartz.JobDataMap MergedJobDataMap { get; } = new Quartz.JobDataMap();
            public CancellationToken CancellationToken => CancellationToken.None;

            public Quartz.IScheduler Scheduler => throw new NotImplementedException();
            public Quartz.ITrigger Trigger
            {
                get
                {
                    var trig = Quartz.TriggerBuilder.Create()
                        .WithIdentity("DummyTrigger", "Group")
                        .Build();
                    return trig;
                }
            }
            public Quartz.ICalendar? Calendar => null;
            public bool Recovering => false;
            public Quartz.TriggerKey TriggerKey => new Quartz.TriggerKey("DummyTrigger", "Group");
            public int RefireCount => 0;
            public Quartz.TriggerKey? RecoveringTriggerKey => null;
            public string FireInstanceId => "Instance_1";
            public Quartz.JobDataMap JobDetailJobDataMap => MergedJobDataMap;
            public Quartz.JobDataMap TriggerJobDataMap => MergedJobDataMap;
            public Quartz.IJobDetail JobDetail => throw new NotImplementedException();
            public Quartz.IJob JobInstance => throw new NotImplementedException();
            public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? ScheduledFireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? PreviousFireTimeUtc => null;
            public DateTimeOffset? NextFireTimeUtc => null;
            public TimeSpan JobRunTime => TimeSpan.Zero;
            public object? Result { get; set; }

            public void Put(object key, object value) { }
            public object Get(object key) => null!;
        }

        private class DummyDialogService : IDialogService
        {
            public bool ConfirmationResult { get; set; } = true;
            public string? MessageTitle { get; private set; }
            public string? MessageBody { get; private set; }

            public Task<Job?> ShowJobEditorAsync(Job? job = null) => Task.FromResult<Job?>(null);
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(ConfirmationResult);
            public Task ShowMessageAsync(string title, string message)
            {
                MessageTitle = title;
                MessageBody = message;
                return Task.CompletedTask;
            }
            public string? SelectFolder(string title = "Klasör Seçin") => null;
            public List<string> SelectFolders(string title = "Klasör Seçin") => new();
            public List<string> SelectFiles(string title = "Dosyaları Seçin") => new();
        }

        private class DummyLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"INFO: {message}");
            public void LogWarning(string message) => Logs.Add($"WARN: {message}");
            public void LogError(string message, Exception? exception = null) => Logs.Add($"ERROR: {message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => string.Empty;
            public void ClearLogs() => Logs.Clear();
        }

        private class DummyNotificationService : INotificationService
        {
            public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info) { }
            public void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? message = null) { }
        }

        private class DummyUsbDriveService : IUsbDriveService
        {
#pragma warning disable CS0067
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
#pragma warning restore CS0067

            public bool IsDriveConnected(string path) => true;
            public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber) => true;
            public string? GetVolumeSerialNumber(string driveLetterOrPath) => null;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber) => null;
            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
        }

        private class DummyJobRepository : IJobRepository
        {
            private readonly List<Job> _jobs = new();
            public DummyJobRepository(IEnumerable<Job> jobs) => _jobs.AddRange(jobs);

            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(_jobs.AsReadOnly());
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));
            public Task AddAsync(Job job) { _jobs.Add(job); return Task.CompletedTask; }
            public Task UpdateAsync(Job job) => Task.CompletedTask;
            public Task DeleteAsync(Guid id) { _jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
        }

        // 1. Running Checkpoint + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule1_RunningCheckpoint_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r1");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Stale Running Job",
                CurrentState = ExecutionState.Running,
                InterruptionReasonCode = ExecutionInterruptionReason.None,
                TotalFiles = 10,
                CompletedFiles = 3,
                PendingFiles = 7,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var job = new Job { Id = jobId, Name = "Stale Running Job", Enabled = true };
            var jobRepo = new DummyJobRepository(new[] { job });
            var dialogService = new DummyDialogService();
            var logService = new DummyLogService();

            var jobsVm = new JobsViewModel(jobRepo, null!, dialogService, logService, null, checkpointRepo);
            await jobsVm.InitializeAsync();

            Assert.True(jobsVm.HasRecoverableJobs);
            Assert.Single(jobsVm.RecoverableCheckpoints);
            Assert.Empty(jobsVm.ActiveJobs);
        }

        // 2. UnexpectedProcessExit + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule2_UnexpectedProcessExit_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r2");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Crashed Job",
                CurrentState = ExecutionState.Running,
                InterruptionReasonCode = ExecutionInterruptionReason.UnexpectedProcessExit,
                TotalFiles = 5,
                CompletedFiles = 1,
                PendingFiles = 4,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
            Assert.Contains("forbidden", gateRes.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // 3. UserStopped + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule3_UserStopped_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r3");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "User Stopped Job",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                CompletedFiles = 2,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
        }

        // 4. UserPaused + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule4_UserPaused_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r4");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "User Paused Job",
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 5,
                CompletedFiles = 2,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
        }

        // 5. DestinationUnavailable + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule5_DestinationUnavailable_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r5");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "USB Lost Job",
                CurrentState = ExecutionState.DestinationUnavailable,
                InterruptionReasonCode = ExecutionInterruptionReason.DestinationUnavailable,
                DestinationWasUnavailable = true,
                TotalFiles = 5,
                CompletedFiles = 1,
                PendingFiles = 4,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
        }

        // 6. Failure Checkpoint + Startup => DISCOVER ONLY, NO auto execution
        [Fact]
        public async Task Rule6_FailureCheckpoint_Startup_DiscoversOnly_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r6");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Failed Job",
                CurrentState = ExecutionState.Failed,
                InterruptionReasonCode = ExecutionInterruptionReason.Failure,
                TotalFiles = 5,
                FailedFiles = 2,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
        }

        // 7. Unresolved Checkpoint + Quartz Misfire => BLOCKED
        [Fact]
        public async Task Rule7_UnresolvedCheckpoint_QuartzMisfire_Blocked()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r7");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Misfire Test Job",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzMisfire);

            Assert.False(gateRes.Allowed);
        }

        // 8. Unresolved Checkpoint + Quartz Scheduled Trigger => BLOCKED
        [Fact]
        public async Task Rule8_UnresolvedCheckpoint_QuartzScheduled_Blocked()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r8");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Scheduled Trigger Job",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzScheduled);

            Assert.False(gateRes.Allowed);
        }

        // 9. Unresolved Checkpoint + Normal RunNow ("Çalıştır") => BLOCKED
        [Fact]
        public async Task Rule9_UnresolvedCheckpoint_NormalRunNow_Blocked()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r9");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Manual Run Job",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.ManualRun);

            Assert.False(gateRes.Allowed);
        }

        // 10. Explicit Devam Et ("RecoveryResume") => ALLOWED exactly once
        [Fact]
        public async Task Rule10_ExplicitRecoveryResume_Allowed()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r10");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Resume Job",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume);

            Assert.True(gateRes.Allowed);
        }

        // 11. Explicit Devam Et => Validated completed files skipped
        [Fact]
        public async Task Rule11_ExplicitRecoveryResume_SkipsCompletedValidatedFiles()
        {
            string srcDir = Path.Combine(_testBaseDir, "src11");
            string destDir = Path.Combine(_testBaseDir, "dest11");
            Directory.CreateDirectory(srcDir);
            string subDestDir = Path.Combine(destDir, "src11");
            Directory.CreateDirectory(subDestDir);

            string file1Src = Path.Combine(srcDir, "file1.txt");
            string file1Dest = Path.Combine(subDestDir, "file1.txt");
            await File.WriteAllTextAsync(file1Src, "Content 1");
            await File.WriteAllTextAsync(file1Dest, "Content 1");
            File.SetLastWriteTimeUtc(file1Dest, File.GetLastWriteTimeUtc(file1Src));

            string file2Src = Path.Combine(srcDir, "file2.txt");
            await File.WriteAllTextAsync(file2Src, "Content 2");

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "cp11"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Resume Skip Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Stopped,
                VerificationMode = VerificationMode.SizeAndTimestamp,
                TotalFiles = 2,
                CompletedFiles = 1,
                PendingFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "file1.txt",
                        SourcePath = file1Src,
                        DestinationPath = file1Dest,
                        SourceLength = new FileInfo(file1Src).Length,
                        SourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(file1Src),
                        Status = CheckpointFileStatus.Completed,
                        BytesCopied = new FileInfo(file1Src).Length
                    },
                    new CheckpointFileEntry
                    {
                        RelativePath = "file2.txt",
                        SourcePath = file2Src,
                        DestinationPath = Path.Combine(subDestDir, "file2.txt"),
                        SourceLength = new FileInfo(file2Src).Length,
                        SourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(file2Src),
                        Status = CheckpointFileStatus.Pending,
                        BytesCopied = 0
                    }
                }
            };
            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job { Id = jobId, Name = "Resume Skip Job", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal(1, result.FilesSkipped);
        }

        // 12. Explicit Devam Et => Pending / Failed files continue
        [Fact]
        public async Task Rule12_ExplicitRecoveryResume_ContinuesPendingAndFailedFiles()
        {
            string srcDir = Path.Combine(_testBaseDir, "src12");
            string destDir = Path.Combine(_testBaseDir, "dest12");
            Directory.CreateDirectory(srcDir);
            string subDestDir = Path.Combine(destDir, "src12");
            Directory.CreateDirectory(subDestDir);

            string file1Src = Path.Combine(srcDir, "failedFile.txt");
            await File.WriteAllTextAsync(file1Src, "Failed Content Fixed");

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "cp12_2"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Resume Failed Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Failed,
                TotalFiles = 1,
                FailedFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "failedFile.txt",
                        SourcePath = file1Src,
                        DestinationPath = Path.Combine(subDestDir, "failedFile.txt"),
                        SourceLength = new FileInfo(file1Src).Length,
                        Status = CheckpointFileStatus.Failed,
                        BytesCopied = 0
                    }
                }
            };
            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job { Id = jobId, Name = "Resume Failed Job", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.True(File.Exists(Path.Combine(subDestDir, "failedFile.txt")));
        }

        // 13. Interrupted current large file => restarts from byte 0, completed files untouched
        [Fact]
        public async Task Rule13_InterruptedCopyingFile_ResetsToPendingOnResume()
        {
            string srcDir = Path.Combine(_testBaseDir, "src13");
            string destDir = Path.Combine(_testBaseDir, "dest13");
            Directory.CreateDirectory(srcDir);
            string subDestDir = Path.Combine(destDir, "src13");
            Directory.CreateDirectory(subDestDir);

            string file1Src = Path.Combine(srcDir, "interrupted.txt");
            await File.WriteAllTextAsync(file1Src, "Partial content interrupted mid-stream");

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "cp13_2"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Interrupted File Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Running,
                TotalFiles = 1,
                PendingFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "interrupted.txt",
                        SourcePath = file1Src,
                        DestinationPath = Path.Combine(subDestDir, "interrupted.txt"),
                        SourceLength = new FileInfo(file1Src).Length,
                        Status = CheckpointFileStatus.Copying,
                        BytesCopied = 10
                    }
                }
            };
            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job { Id = jobId, Name = "Interrupted File Job", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal("Partial content interrupted mid-stream", await File.ReadAllTextAsync(Path.Combine(subDestDir, "interrupted.txt")));
        }

        // 14. Destination disappears and returns during SAME ACTIVE SESSION => Allowed
        [Fact]
        public async Task Rule14_SameSession_ActiveUsbReturn_Allowed()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r14");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Active USB Job",
                CurrentState = ExecutionState.DestinationUnavailable,
                InterruptionReasonCode = ExecutionInterruptionReason.DestinationUnavailable,
                DestinationWasUnavailable = true,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true,
                ProcessInstanceId = JobExecutionGate.CurrentProcessInstanceId
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.DestinationReturnedSameActiveSession);

            Assert.True(gateRes.Allowed);
        }

        // 15. DestinationUnavailable after APP RESTART => Manual Devam Et required (Blocked on startup/scheduled)
        [Fact]
        public async Task Rule15_RestartedSession_UsbReturn_Blocked()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r15");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Restarted USB Job",
                CurrentState = ExecutionState.DestinationUnavailable,
                InterruptionReasonCode = ExecutionInterruptionReason.DestinationUnavailable,
                DestinationWasUnavailable = true,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);

            var gateResRestart = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.DestinationReturnedAfterRestart);
            Assert.False(gateResRestart.Allowed);

            var gateResStartup = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);
            Assert.False(gateResStartup.Allowed);
        }

        // 16. UserStopped never enters automatic retry
        [Fact]
        public async Task Rule16_UserStopped_NeverEntersAutoRetry()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r16");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "User Stopped Retry Job",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.Retry);

            Assert.False(gateRes.Allowed);
        }

        // 17. UserPaused never enters automatic retry
        [Fact]
        public async Task Rule17_UserPaused_NeverEntersAutoRetry()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r17");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "User Paused Retry Job",
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.Retry);

            Assert.False(gateRes.Allowed);
        }

        // 18. UserCancelled never recovers automatically
        [Fact]
        public async Task Rule18_UserCancelled_NeverAutoRecovers()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r18");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "User Cancelled Job",
                CurrentState = ExecutionState.Cancelled,
                InterruptionReasonCode = ExecutionInterruptionReason.UserCancelled,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);

            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzScheduled)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.ManualRun)).Allowed);
        }

        // 19. Job A has unresolved checkpoint, Job B healthy => Job A blocked, Job B schedules normally
        [Fact]
        public async Task Rule19_JobAUnresolved_JobBHealthy_JobBSchedulesNormally()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r19");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobAId = Guid.NewGuid();
            var jobBId = Guid.NewGuid();

            var cpA = new JobCheckpoint
            {
                JobId = jobAId,
                JobName = "Job A (Blocked)",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cpA);

            var gate = new JobExecutionGate(checkpointRepo);

            var resA = await gate.CanExecuteAsync(jobAId, ExecutionTriggerSource.QuartzScheduled);
            Assert.False(resA.Allowed);

            var resB = await gate.CanExecuteAsync(jobBId, ExecutionTriggerSource.QuartzScheduled);
            Assert.True(resB.Allowed);
        }

        // 20. Legacy Running checkpoint => discovered & displayed, NOT automatically executed
        [Fact]
        public async Task Rule20_LegacyRunningCheckpoint_Discovered_NoAutoExecution()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "r20");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            // Legacy JSON checkpoint without InterruptionReasonCode property set (defaults to 0 / None)
            var legacyCp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Legacy Checkpoint",
                CurrentState = ExecutionState.Running,
                InterruptionReasonCode = ExecutionInterruptionReason.None,
                TotalFiles = 4,
                CompletedFiles = 1,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(legacyCp);

            // Verify backward compatibility helper
            Assert.Equal(ExecutionInterruptionReason.UnexpectedProcessExit, legacyCp.GetEffectiveInterruptionReason());

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);

            Assert.False(gateRes.Allowed);
        }

        // Integration TEST A: Full lifecycle with UserStopped -> Restart host -> Quartz start -> FileCopyService NOT invoked
        [Fact]
        public async Task IntegrationTestA_UserStopped_FullLifecycle_RestartHost_Quartz_DoesNotExecute()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleA");
            Directory.CreateDirectory(storageDir);
            var jobId = Guid.NewGuid();

            // 1. First Host Session: Save UserStopped Checkpoint
            var cpRepo1 = new CheckpointRepository(customDirectory: storageDir);
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Lifecycle Job A",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo1.SaveCheckpointAsync(cp);

            // 2. Host Shutdown / Spin up NEW Host Instance with SAME storage
            var cpRepo2 = new CheckpointRepository(customDirectory: storageDir);
            var logService2 = new DummyLogService();
            var gate2 = new JobExecutionGate(cpRepo2, logService2);
            var dummyFileCopy = new FileCopyService(logService: logService2, checkpointRepository: cpRepo2, jobExecutionGate: gate2);
            var job = new Job { Id = jobId, Name = "Lifecycle Job A", Enabled = true, MissedJobBehavior = MissedJobBehavior.RunImmediately };
            var jobRepo2 = new DummyJobRepository(new[] { job });

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo2,
                fileCopyService: dummyFileCopy,
                historyRepository: new HistoryRepository(Path.Combine(storageDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService2,
                jobExecutionManager: null,
                checkpointRepository: cpRepo2,
                jobExecutionGate: gate2);

            var mockContext = new DummyJobExecutionContext();
            mockContext.MergedJobDataMap.Put("JobId", jobId.ToString());

            // Quartz trigger fires after startup
            await quartzJob.Execute(mockContext);

            // Assert FileCopyService REFUSED/BLOCKED execution and checkpoint remains UserStopped
            var checkpointOnDisk = await cpRepo2.GetCheckpointAsync(jobId);
            Assert.NotNull(checkpointOnDisk);
            Assert.Equal(ExecutionState.Stopped, checkpointOnDisk!.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserStopped, checkpointOnDisk.GetEffectiveInterruptionReason());
            Assert.Contains(logService2.Logs, l => l.Contains("GATE REJECTED") || l.Contains("REFUSED EXECUTION") || l.Contains("DISCOVER_ONLY") || l.Contains("BLOCKED"));
        }

        // Integration TEST B: Full lifecycle with UserPaused -> Restart host -> Quartz start -> FileCopyService NOT invoked
        [Fact]
        public async Task IntegrationTestB_UserPaused_FullLifecycle_RestartHost_Quartz_DoesNotExecute()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleB");
            Directory.CreateDirectory(storageDir);
            var jobId = Guid.NewGuid();

            var cpRepo1 = new CheckpointRepository(customDirectory: storageDir);
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Lifecycle Job B",
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo1.SaveCheckpointAsync(cp);

            var cpRepo2 = new CheckpointRepository(customDirectory: storageDir);
            var logService2 = new DummyLogService();
            var gate2 = new JobExecutionGate(cpRepo2, logService2);
            var dummyFileCopy = new FileCopyService(logService: logService2, checkpointRepository: cpRepo2, jobExecutionGate: gate2);
            var job = new Job { Id = jobId, Name = "Lifecycle Job B", Enabled = true };
            var jobRepo2 = new DummyJobRepository(new[] { job });

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo2,
                fileCopyService: dummyFileCopy,
                historyRepository: new HistoryRepository(Path.Combine(storageDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService2,
                jobExecutionManager: null,
                checkpointRepository: cpRepo2,
                jobExecutionGate: gate2);

            var mockContext = new DummyJobExecutionContext();
            mockContext.MergedJobDataMap.Put("JobId", jobId.ToString());

            await quartzJob.Execute(mockContext);

            var checkpointOnDisk = await cpRepo2.GetCheckpointAsync(jobId);
            Assert.NotNull(checkpointOnDisk);
            Assert.Equal(ExecutionState.Paused, checkpointOnDisk!.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserPaused, checkpointOnDisk.GetEffectiveInterruptionReason());
        }

        // Integration TEST C: Full lifecycle with stale Running checkpoint -> Restart host -> DISCOVER ONLY, no FileCopyService
        [Fact]
        public async Task IntegrationTestC_StaleRunning_FullLifecycle_RestartHost_DiscoversOnly_NoAutoExecution()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleC");
            Directory.CreateDirectory(storageDir);
            var jobId = Guid.NewGuid();

            var cpRepo1 = new CheckpointRepository(customDirectory: storageDir);
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Lifecycle Job C",
                CurrentState = ExecutionState.Running,
                InterruptionReasonCode = ExecutionInterruptionReason.UnexpectedProcessExit,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo1.SaveCheckpointAsync(cp);

            var cpRepo2 = new CheckpointRepository(customDirectory: storageDir);
            var logService2 = new DummyLogService();
            var gate2 = new JobExecutionGate(cpRepo2, logService2);

            var gateRes = await gate2.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery);
            Assert.False(gateRes.Allowed);
            Assert.Contains("forbidden", gateRes.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // Integration TEST D: Unresolved checkpoint + Quartz misfire -> No execution
        [Fact]
        public async Task IntegrationTestD_UnresolvedCheckpoint_QuartzMisfire_NoExecution()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleD");
            Directory.CreateDirectory(storageDir);
            var jobId = Guid.NewGuid();

            var cpRepo = new CheckpointRepository(customDirectory: storageDir);
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Misfire Job D",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(cpRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzMisfire);

            Assert.False(gateRes.Allowed);
        }

        // Integration TEST E: Unresolved checkpoint + MissedJobBehavior.RunImmediately -> No execution
        [Fact]
        public async Task IntegrationTestE_UnresolvedCheckpoint_MissedJobRunImmediately_NoExecution()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleE");
            Directory.CreateDirectory(storageDir);
            var jobId = Guid.NewGuid();

            var cpRepo = new CheckpointRepository(customDirectory: storageDir);
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "RunImmediately Job E",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var logService = new DummyLogService();
            var gate = new JobExecutionGate(cpRepo, logService);
            var job = new Job { Id = jobId, Name = "RunImmediately Job E", MissedJobBehavior = MissedJobBehavior.RunImmediately };
            var jobRepo = new DummyJobRepository(new[] { job });
            var dummyFileCopy = new FileCopyService(logService: logService, checkpointRepository: cpRepo, jobExecutionGate: gate);

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo,
                fileCopyService: dummyFileCopy,
                historyRepository: new HistoryRepository(Path.Combine(storageDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService,
                jobExecutionManager: null,
                checkpointRepository: cpRepo,
                jobExecutionGate: gate);

            var mockContext = new DummyJobExecutionContext();
            mockContext.MergedJobDataMap.Put("JobId", jobId.ToString());

            await quartzJob.Execute(mockContext);

            Assert.Contains(logService.Logs, l => l.Contains("GATE REJECTED") || l.Contains("REFUSED EXECUTION") || l.Contains("DISCOVER_ONLY") || l.Contains("BLOCKED"));
        }

        // Integration TEST F: Job A unresolved checkpoint, Job B healthy -> A blocked, B executes
        [Fact]
        public async Task IntegrationTestF_JobABlocked_JobBExecuted()
        {
            string storageDir = Path.Combine(_testBaseDir, "lifecycleF");
            Directory.CreateDirectory(storageDir);

            string srcDirB = Path.Combine(_testBaseDir, "srcB");
            string destDirB = Path.Combine(_testBaseDir, "destB");
            Directory.CreateDirectory(srcDirB);
            Directory.CreateDirectory(destDirB);
            File.WriteAllText(Path.Combine(srcDirB, "jobB.txt"), "Job B Data");

            var jobAId = Guid.NewGuid();
            var jobBId = Guid.NewGuid();

            var cpRepo = new CheckpointRepository(customDirectory: storageDir);
            var cpA = new JobCheckpoint
            {
                JobId = jobAId,
                JobName = "Job A",
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cpA);

            var logService = new DummyLogService();
            var gate = new JobExecutionGate(cpRepo, logService);
            var jobA = new Job { Id = jobAId, Name = "Job A", Enabled = true };
            var jobB = new Job { Id = jobBId, Name = "Job B", SourcePaths = new List<string> { srcDirB }, DestinationPath = destDirB, Enabled = true };
            var jobRepo = new DummyJobRepository(new[] { jobA, jobB });
            var fileCopyService = new FileCopyService(logService: logService, checkpointRepository: cpRepo, jobExecutionGate: gate);

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo,
                fileCopyService: fileCopyService,
                historyRepository: new HistoryRepository(Path.Combine(storageDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService,
                jobExecutionManager: null,
                checkpointRepository: cpRepo,
                jobExecutionGate: gate);

            // 1. Execute Job A -> GATE REJECTS
            var contextA = new DummyJobExecutionContext();
            contextA.MergedJobDataMap.Put("JobId", jobAId.ToString());
            await quartzJob.Execute(contextA);
            Assert.Contains(logService.Logs, l => l.Contains("GATE REJECTED") || l.Contains("REFUSED EXECUTION") || l.Contains("DISCOVER_ONLY") || l.Contains("BLOCKED"));

            // 2. Execute Job B -> ALLOWED & EXECUTES
            var contextB = new DummyJobExecutionContext();
            contextB.MergedJobDataMap.Put("JobId", jobBId.ToString());
            await quartzJob.Execute(contextB);
            Assert.True(File.Exists(Path.Combine(destDirB, "srcB", "jobB.txt")));
        }

        [Fact]
        public void ProgressThrottler_BaselineBytes_CalculatesSpeedOnlyFromSessionBytes()
        {
            FileCopyProgress? reportedProgress = null;
            var mockProgress = new Progress<FileCopyProgress>(p => reportedProgress = p);

            long totalBytes = 10_000_000; // 10 MB
            long validatedBaselineBytes = 6_000_000; // 6 MB baseline from previous run

            var throttler = new ProgressThrottler(mockProgress, new Job { Name = "Speed Test Job" }, totalFiles: 10, totalBytes: totalBytes, validatedBaselineBytes: validatedBaselineBytes);
            throttler.ReportStart();

            // Simulate copying 100 KB in current session
            long sessionCopiedChunk = 100_000;
            throttler.ReportChunk((int)sessionCopiedChunk, currentFileBytesCopied: sessionCopiedChunk, currentFileSize: 500_000);

            // Wait brief instant and trigger update
            Thread.Sleep(50);
            throttler.ReportChunk(100, currentFileBytesCopied: sessionCopiedChunk + 100, currentFileSize: 500_000);

            Assert.Equal(validatedBaselineBytes + sessionCopiedChunk + 100, throttler.ProgressData.BytesCopied);
            Assert.Equal(61.0, throttler.ProgressData.Percentage, 1);

            // Speed should NOT be based on 6.1 MB / 0.05s (~122 MB/s), but rather on ~100 KB / 0.05s (~2 MB/s)
            double bytesPerSec = throttler.ProgressData.BytesPerSecond;
            Assert.True(bytesPerSec < 20_000_000, $"Speed was incorrectly inflated: {bytesPerSec} B/s");
        }

        [Fact]
        public void ProgressThrottler_PausedState_ReportsZeroSpeed()
        {
            FileCopyProgress? reportedProgress = null;
            var mockProgress = new Progress<FileCopyProgress>(p => reportedProgress = p);

            var throttler = new ProgressThrottler(mockProgress, new Job { Name = "Pause Speed Test" }, totalFiles: 5, totalBytes: 5_000_000);
            throttler.ReportStart();

            throttler.ReportChunk(500_000, 500_000, 1_000_000);
            throttler.ReportState(ExecutionState.Paused, "Duraklatıldı");

            Assert.Equal(0, throttler.ProgressData.BytesPerSecond);
        }

        [Fact]
        public async Task ScheduledJob_UserPaused_StartupHostInstance_DoesNotAutoResume_StaysPaused()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "paused_host_test");
            var cpRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            string srcDir = Path.Combine(_testBaseDir, "src_p");
            string destDir = Path.Combine(_testBaseDir, "dest_p");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1Src = Path.Combine(srcDir, "file1.txt");
            string file2Src = Path.Combine(srcDir, "file2.txt");
            await File.WriteAllTextAsync(file1Src, "File 1 Data");
            await File.WriteAllTextAsync(file2Src, "File 2 Data");

            // Checkpoint where file 1 was completed, file 2 pending, and state is Paused (UserPaused)
            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Paused Job Test",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 2,
                CompletedFiles = 1,
                PendingFiles = 1,
                IsRecoverable = true,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry { SourcePath = file1Src, Status = CheckpointFileStatus.Completed, BytesCopied = 11 },
                    new CheckpointFileEntry { SourcePath = file2Src, Status = CheckpointFileStatus.Pending, BytesCopied = 0 }
                }
            };
            await cpRepo.SaveCheckpointAsync(cp);

            // Host startup simulation
            var gate = new JobExecutionGate(cpRepo);
            var job = new Job { Id = jobId, Name = "Paused Job Test", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir, Enabled = true };
            var jobRepo = new DummyJobRepository(new[] { job });
            var mockFileCopyService = new DummyFileCopyService();
            var logService = new DummyLogService();

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo,
                fileCopyService: mockFileCopyService,
                historyRepository: new HistoryRepository(Path.Combine(checkpointsDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService,
                jobExecutionManager: null,
                checkpointRepository: cpRepo,
                jobExecutionGate: gate);

            // Quartz triggers on schedule/misfire (TriggerSource = QuartzScheduled)
            var context = new DummyJobExecutionContext();
            context.MergedJobDataMap.Put("JobId", jobId.ToString());
            await quartzJob.Execute(context);

            // Assert FileCopyService was NEVER invoked and checkpoint remains Paused
            Assert.Equal(0, mockFileCopyService.InvocationCount);
            var reloadedCp = await cpRepo.GetCheckpointAsync(jobId);
            Assert.NotNull(reloadedCp);
            Assert.Equal(ExecutionState.Paused, reloadedCp!.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserPaused, reloadedCp.InterruptionReasonCode);
        }

        [Fact]
        public async Task ScheduledJob_UserStopped_StartupHostInstance_DoesNotAutoResume_StaysStopped()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "stopped_host_test");
            var cpRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            string srcDir = Path.Combine(_testBaseDir, "src_s");
            string destDir = Path.Combine(_testBaseDir, "dest_s");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Stopped Job Test",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Stopped,
                InterruptionReasonCode = ExecutionInterruptionReason.UserStopped,
                TotalFiles = 2,
                CompletedFiles = 1,
                PendingFiles = 1,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(cpRepo);
            var job = new Job { Id = jobId, Name = "Stopped Job Test", Enabled = true };
            var jobRepo = new DummyJobRepository(new[] { job });
            var mockFileCopyService = new DummyFileCopyService();
            var logService = new DummyLogService();

            var quartzJob = new QuartzCopyJob(
                jobRepository: jobRepo,
                fileCopyService: mockFileCopyService,
                historyRepository: new HistoryRepository(Path.Combine(checkpointsDir, "hist")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logService,
                jobExecutionManager: null,
                checkpointRepository: cpRepo,
                jobExecutionGate: gate);

            var context = new DummyJobExecutionContext();
            context.MergedJobDataMap.Put("JobId", jobId.ToString());
            await quartzJob.Execute(context);

            Assert.Equal(0, mockFileCopyService.InvocationCount);
            var reloadedCp = await cpRepo.GetCheckpointAsync(jobId);
            Assert.NotNull(reloadedCp);
            Assert.Equal(ExecutionState.Stopped, reloadedCp!.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserStopped, reloadedCp.InterruptionReasonCode);
        }

        [Fact]
        public async Task FileCopyService_RefusesExecution_WhenUnfinishedCheckpointExists_AndTriggerSourceIsNotRecoveryResume()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "refusal_test");
            var cpRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            string srcDir = Path.Combine(_testBaseDir, "src_refuse");
            string destDir = Path.Combine(_testBaseDir, "dest_refuse");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "test.txt"), "Data");

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Refusal Test Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 1,
                PendingFiles = 1,
                IsRecoverable = true
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var service = new FileCopyService(logService: null, checkpointRepository: cpRepo);
            var job = new Job { Id = jobId, Name = "Refusal Test Job", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir };

            // Call CopyAsync directly with QuartzScheduled trigger source (non-RecoveryResume)
            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: cp, triggerSource: ExecutionTriggerSource.QuartzScheduled);

            Assert.False(result.Success);
            Assert.Equal(JobResultStatus.Cancelled, result.Status);
            Assert.Contains(result.Errors, e => e.Contains("Tamamlanmamış veya durdurulmuş kopyalama kaydı mevcut"));
        }

        // ============================================================
        // V9 REGRESSION TESTS — STARTUP AUTO-RESUME & DUPLICATION & LIFECYCLE
        // ============================================================

        [Theory]
        [InlineData(ExecutionState.Paused, ExecutionInterruptionReason.UserPaused)]
        [InlineData(ExecutionState.Stopped, ExecutionInterruptionReason.UserStopped)]
        [InlineData(ExecutionState.DestinationUnavailable, ExecutionInterruptionReason.DestinationUnavailable)]
        [InlineData(ExecutionState.Running, ExecutionInterruptionReason.UnexpectedProcessExit)]
        [InlineData(ExecutionState.Failed, ExecutionInterruptionReason.Failure)]
        public async Task Startup_AllInterruptionStates_DiscoveredOnly_NeverAutomaticallyExecuted(ExecutionState state, ExecutionInterruptionReason reason)
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "startup_state_" + state);
            var cpRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "State Test " + state,
                CurrentState = state,
                InterruptionReasonCode = reason,
                TotalFiles = 5,
                CompletedFiles = 2,
                PendingFiles = 3,
                IsRecoverable = true,
                ProcessInstanceId = Guid.NewGuid() // Old process run
            };
            await cpRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(cpRepo);

            // Startup triggers must be BLOCKED
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.StartupRecovery)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzScheduled)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzMisfire)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.ManualRun)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.Retry)).Allowed);
            Assert.False((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.DestinationReturnedAfterRestart)).Allowed);

            // ONLY Explicit Recovery Resume is ALLOWED
            Assert.True((await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume)).Allowed);
        }

        [Fact]
        public async Task RecoveryDiscovery_Deduplication_IdempotentAcrossMultipleCallsAndSameJobId()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "dedup_test");
            var cpRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId1 = Guid.NewGuid();
            var jobId2 = Guid.NewGuid();

            // Save checkpoint for jobId1
            var cp1 = new JobCheckpoint
            {
                JobId = jobId1,
                JobName = "Yeni Kopyalama Görevi", // Identical display name
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 10,
                PendingFiles = 5,
                IsRecoverable = true,
                UpdatedAt = DateTime.Now.AddMinutes(-5)
            };
            await cpRepo.SaveCheckpointAsync(cp1);

            // Save second updated checkpoint for same jobId1
            var cp1Updated = new JobCheckpoint
            {
                JobId = jobId1,
                JobName = "Yeni Kopyalama Görevi",
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 10,
                PendingFiles = 4,
                IsRecoverable = true,
                UpdatedAt = DateTime.Now
            };
            await cpRepo.SaveCheckpointAsync(cp1Updated);

            // Save checkpoint for different jobId2 with same display name
            var cp2 = new JobCheckpoint
            {
                JobId = jobId2,
                JobName = "Yeni Kopyalama Görevi", // Identical display name but different JobId
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 20,
                PendingFiles = 10,
                IsRecoverable = true,
                UpdatedAt = DateTime.Now
            };
            await cpRepo.SaveCheckpointAsync(cp2);

            // Test Repository Deduplication
            var repoRecoverables = await cpRepo.GetRecoverableCheckpointsAsync();
            Assert.Equal(2, repoRecoverables.Count); // Exactly 2 distinct JobIds

            // Test ViewModel Discovery Idempotency
            var job1 = new Job { Id = jobId1, Name = "Yeni Kopyalama Görevi" };
            var job2 = new Job { Id = jobId2, Name = "Yeni Kopyalama Görevi" };
            var jobRepo = new DummyJobRepository(new[] { job1, job2 });
            var dialogService = new DummyDialogService();
            var logService = new DummyLogService();
            var jobsVm = new JobsViewModel(jobRepo, new JobScheduler(new CustomQuartzJobFactory(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()), logService), dialogService, logService, checkpointRepository: cpRepo);

            // Discover 1st time
            await jobsVm.LoadJobsAsync();
            Assert.Equal(2, jobsVm.RecoverableCheckpoints.Count);

            // Discover 2nd time
            await jobsVm.LoadJobsAsync();
            Assert.Equal(2, jobsVm.RecoverableCheckpoints.Count);

            // Discover 3rd time
            await jobsVm.LoadJobsAsync();
            Assert.Equal(2, jobsVm.RecoverableCheckpoints.Count);
        }

        [Fact]
        public async Task CriticalEndToEndLifecycle_SessionA_Pause_Shutdown_SessionB_Discover_AssertNoAutoRun_Resume_Pause_SessionC_Discover()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "lifecycle_e2e");
            Directory.CreateDirectory(checkpointsDir);
            var jobId = Guid.NewGuid();
            var job = new Job { Id = jobId, Name = "Lifecycle Job", Enabled = true };

            // -------------------------------------------------------------
            // SESSION A: Start Job X -> Pause Job X -> Persist Checkpoint -> Shutdown
            // -------------------------------------------------------------
            var cpRepoA = new CheckpointRepository(customDirectory: checkpointsDir);
            var cpA = new JobCheckpoint
            {
                JobId = jobId,
                JobName = job.Name,
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 5,
                PendingFiles = 3,
                IsRecoverable = true,
                ProcessInstanceId = Guid.NewGuid() // Session A process identity
            };
            await cpRepoA.SaveCheckpointAsync(cpA);

            // -------------------------------------------------------------
            // SESSION B: Launch fresh process/session -> Start Scheduler -> Discover
            // -------------------------------------------------------------
            var cpRepoB = new CheckpointRepository(customDirectory: checkpointsDir);
            var gateB = new JobExecutionGate(cpRepoB);
            var mockFileCopyServiceB = new DummyFileCopyService();
            var logServiceB = new DummyLogService();
            var jobRepoB = new DummyJobRepository(new[] { job });

            var quartzJobB = new QuartzCopyJob(
                jobRepository: jobRepoB,
                fileCopyService: mockFileCopyServiceB,
                historyRepository: new HistoryRepository(Path.Combine(checkpointsDir, "histB")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logServiceB,
                jobExecutionManager: null,
                checkpointRepository: cpRepoB,
                jobExecutionGate: gateB);

            // Simulate Quartz trigger firing automatically at startup in Session B
            var contextB = new DummyJobExecutionContext();
            contextB.MergedJobDataMap.Put("JobId", jobId.ToString());
            await quartzJobB.Execute(contextB);

            // ASSERT: CopyAsync invocation count == 0
            Assert.Equal(0, mockFileCopyServiceB.InvocationCount);

            // Discover Recovery Card in Session B UI
            var jobsVmB = new JobsViewModel(jobRepoB, new JobScheduler(new CustomQuartzJobFactory(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()), logServiceB), new DummyDialogService(), logServiceB, checkpointRepository: cpRepoB);
            await jobsVmB.LoadJobsAsync();

            // ASSERT: Recovery entry count == 1
            Assert.Single(jobsVmB.RecoverableCheckpoints);
            Assert.Equal(jobId, jobsVmB.RecoverableCheckpoints.First().JobId);

            // Now explicitly invoke RecoveryResume (User clicks "Devam Et")
            contextB.MergedJobDataMap.Put("IsRecoveryResume", true);
            await quartzJobB.Execute(contextB);

            // ASSERT: CopyAsync invocation count == 1
            Assert.Equal(1, mockFileCopyServiceB.InvocationCount);

            // Pause again in Session B
            var cpBUpdated = new JobCheckpoint
            {
                JobId = jobId,
                JobName = job.Name,
                CurrentState = ExecutionState.Paused,
                InterruptionReasonCode = ExecutionInterruptionReason.UserPaused,
                TotalFiles = 5,
                PendingFiles = 2,
                IsRecoverable = true,
                ProcessInstanceId = Guid.NewGuid() // Session B process identity
            };
            await cpRepoB.SaveCheckpointAsync(cpBUpdated);

            // Shutdown Session B.

            // -------------------------------------------------------------
            // SESSION C: Launch fresh process/session -> Discover
            // -------------------------------------------------------------
            var cpRepoC = new CheckpointRepository(customDirectory: checkpointsDir);
            var gateC = new JobExecutionGate(cpRepoC);
            var mockFileCopyServiceC = new DummyFileCopyService();
            var logServiceC = new DummyLogService();
            var jobRepoC = new DummyJobRepository(new[] { job });

            var quartzJobC = new QuartzCopyJob(
                jobRepository: jobRepoC,
                fileCopyService: mockFileCopyServiceC,
                historyRepository: new HistoryRepository(Path.Combine(checkpointsDir, "histC")),
                notificationService: new DummyNotificationService(),
                usbDriveService: new DummyUsbDriveService(),
                logService: logServiceC,
                jobExecutionManager: null,
                checkpointRepository: cpRepoC,
                jobExecutionGate: gateC);

            // Simulate Quartz trigger firing automatically at startup in Session C
            var contextC = new DummyJobExecutionContext();
            contextC.MergedJobDataMap.Put("JobId", jobId.ToString());
            await quartzJobC.Execute(contextC);

            // ASSERT: CopyAsync invocation count == 0
            Assert.Equal(0, mockFileCopyServiceC.InvocationCount);

            // Discover Recovery Card in Session C UI
            var jobsVmC = new JobsViewModel(jobRepoC, new JobScheduler(new CustomQuartzJobFactory(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()), logServiceC), new DummyDialogService(), logServiceC, checkpointRepository: cpRepoC);
            await jobsVmC.LoadJobsAsync();

            // ASSERT: Recovery entry count == 1
            Assert.Single(jobsVmC.RecoverableCheckpoints);
            Assert.Equal(jobId, jobsVmC.RecoverableCheckpoints.First().JobId);
        }

        private class DummyFileCopyService : IFileCopyService
        {
            public int InvocationCount { get; private set; }

            public Task<FileCopyResult> CopyAsync(Job job, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.QuartzScheduled)
            {
                InvocationCount++;
                return Task.FromResult(new FileCopyResult { Success = true, Status = JobResultStatus.Success });
            }

            public Task<FileCopyResult> CopyAsync(IEnumerable<string> sourcePaths, string destinationPath, CopyMode copyMode, ConflictPolicy conflictPolicy, bool dryRun, IProgress<double>? progress, CancellationToken cancellationToken)
            {
                InvocationCount++;
                return Task.FromResult(new FileCopyResult { Success = true, Status = JobResultStatus.Success });
            }

            public Task<FileCopyResult> RetryFailedFilesAsync(Job job, List<FileItemResult> failedItems, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.HistoryRetry)
            {
                InvocationCount++;
                return Task.FromResult(new FileCopyResult { Success = true, Status = JobResultStatus.Success });
            }
        }
    }
}
