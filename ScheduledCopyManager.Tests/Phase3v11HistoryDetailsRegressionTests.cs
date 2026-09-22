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
    public class Phase3v11HistoryDetailsRegressionTests : IDisposable
    {
        private readonly string _testRootDir;

        public Phase3v11HistoryDetailsRegressionTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "v11HistoryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRootDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testRootDir))
                    Directory.Delete(_testRootDir, true);
            }
            catch { }
        }

        private class DummyDialogService : IDialogService
        {
            public string? MessageTitle { get; private set; }
            public string? MessageBody { get; private set; }

            public Task<Job?> ShowJobEditorAsync(Job? job = null) => Task.FromResult<Job?>(null);
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(true);
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

        private class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        // A. Cancel during first large file
        [Fact]
        public async Task TestA_CancelDuringFirstLargeFile_PersistsIncompleteHistory()
        {
            var srcDir = Path.Combine(_testRootDir, "cancel1_src");
            var destDir = Path.Combine(_testRootDir, "cancel1_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "large1.dat");
            byte[] largeData = new byte[200 * 1024 * 1024]; // 200 MB
            new Random(42).NextBytes(largeData);
            await File.WriteAllBytesAsync(file1, largeData);

            var service = new FileCopyService(new DummyLogService());
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Cancel1 Job",
                SourcePaths = new List<string> { file1 },
                DestinationPath = destDir,
                BandwidthLimit = new BandwidthLimit { Enabled = true, MegabytesPerSecond = 5.0 }
            };

            using var cts = new CancellationTokenSource();
            string tempFile = Path.Combine(destDir, "large1.dat.tmp");

            var cancelTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (File.Exists(tempFile))
                    {
                        try
                        {
                            var fi = new FileInfo(tempFile);
                            if (fi.Length > 0)
                            {
                                cts.Cancel();
                                break;
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(1);
                }
            });

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, false, null, cts.Token);
            });

            await cancelTask;

            Assert.NotNull(ex.Data["FileCopyResult"]);
            var res = (FileCopyResult)ex.Data["FileCopyResult"]!;

            Assert.Equal(0, res.FilesCopied);
            Assert.Equal(1, res.FilesIncomplete);
            Assert.True(res.BytesCopied > 0);
            Assert.Equal(JobResultStatus.Cancelled, res.Status);
            Assert.Single(res.FileResults);
            Assert.Equal(FileItemStatus.Incomplete, res.FileResults[0].Status);
            Assert.True(res.FileResults[0].BytesTransferred > 0);
        }

        // B. Complete two files, cancel during third
        [Fact]
        public async Task TestB_CompleteTwoFilesCancelDuringThird_PersistsAllFileResults()
        {
            var srcDir = Path.Combine(_testRootDir, "cancel3_src");
            var destDir = Path.Combine(_testRootDir, "cancel3_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "file1.txt");
            string file2 = Path.Combine(srcDir, "file2.txt");
            string file3 = Path.Combine(srcDir, "large3.dat");

            await File.WriteAllTextAsync(file1, "Content 1");
            await File.WriteAllTextAsync(file2, "Content 2");

            byte[] largeData = new byte[200 * 1024 * 1024]; // 200 MB
            new Random(42).NextBytes(largeData);
            await File.WriteAllBytesAsync(file3, largeData);

            var service = new FileCopyService(new DummyLogService());
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Cancel3 Job",
                SourcePaths = new List<string> { file1, file2, file3 },
                DestinationPath = destDir,
                BandwidthLimit = new BandwidthLimit { Enabled = true, MegabytesPerSecond = 5.0 }
            };

            using var cts = new CancellationTokenSource();
            string tempFile = Path.Combine(destDir, "large3.dat.tmp");

            var cancelTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (File.Exists(tempFile))
                    {
                        try
                        {
                            var fi = new FileInfo(tempFile);
                            if (fi.Length > 0)
                            {
                                cts.Cancel();
                                break;
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(1);
                }
            });

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, false, null, cts.Token);
            });

            await cancelTask;

            var res = (FileCopyResult)ex.Data["FileCopyResult"]!;

            Assert.Equal(2, res.FilesCopied);
            Assert.Equal(1, res.FilesIncomplete);
            Assert.Equal(JobResultStatus.Cancelled, res.Status);
            Assert.Equal(3, res.FileResults.Count);

            Assert.Equal(FileItemStatus.Completed, res.FileResults[0].Status);
            Assert.Equal(FileItemStatus.Completed, res.FileResults[1].Status);
            Assert.Equal(FileItemStatus.Incomplete, res.FileResults[2].Status);
            Assert.True(res.BytesCopied > 0);
        }

        // C. Failure on one file, continue others
        [Fact]
        public async Task TestC_FailureOnOneFileContinueOthers_PersistsFailedAndSuccessfulRows()
        {
            var srcDir = Path.Combine(_testRootDir, "fail_src");
            var destDir = Path.Combine(_testRootDir, "fail_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "file1.txt");
            string file2 = Path.Combine(srcDir, "file2.txt");
            string file3 = Path.Combine(srcDir, "file3.txt");

            await File.WriteAllTextAsync(file1, "Valid 1");
            await File.WriteAllTextAsync(file2, "Corrupt Data");
            await File.WriteAllTextAsync(file3, "Valid 3");

            // Lock file2 exclusively so reading it during copy fails
            using var lockStream = new FileStream(file2, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var service = new FileCopyService(new DummyLogService());
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Fail Job",
                SourcePaths = new List<string> { file1, file2, file3 },
                DestinationPath = destDir,
                ContinueOnError = true,
                RetryCount = 1
            };

            var res = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.Equal(2, res.FilesCopied);
            Assert.Equal(1, res.FilesFailed);
            Assert.Equal(JobResultStatus.PartialSuccess, res.Status);
            Assert.Equal(3, res.FileResults.Count);
        }

        // D. Skipped file
        [Fact]
        public async Task TestD_SkippedFile_PersistsSkippedRowAndCounters()
        {
            var srcDir = Path.Combine(_testRootDir, "skip_src");
            var destDir = Path.Combine(_testRootDir, "skip_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "file1.txt");
            await File.WriteAllTextAsync(file1, "Existing");

            string targetFolder = Path.Combine(destDir, "skip_src");
            Directory.CreateDirectory(targetFolder);
            await File.WriteAllTextAsync(Path.Combine(targetFolder, "file1.txt"), "Existing");

            var service = new FileCopyService(new DummyLogService());
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Skip Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                ConflictPolicy = ConflictPolicy.Skip
            };

            var res = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.Equal(0, res.FilesCopied);
            Assert.Equal(1, res.FilesSkipped);
            Assert.Equal(JobResultStatus.Success, res.Status);
            Assert.Single(res.FileResults);
            Assert.Equal(FileItemStatus.Skipped, res.FileResults[0].Status);
        }

        // E. Successful execution
        [Fact]
        public async Task TestE_SuccessfulExecution_PersistsAllSuccessRows()
        {
            var srcDir = Path.Combine(_testRootDir, "ok_src");
            var destDir = Path.Combine(_testRootDir, "ok_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            await File.WriteAllTextAsync(Path.Combine(srcDir, "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "b.txt"), "b");

            var service = new FileCopyService(new DummyLogService());
            var job = new Job { Id = Guid.NewGuid(), Name = "Success Job", SourcePaths = new List<string> { srcDir }, DestinationPath = destDir };

            var res = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res.Success);
            Assert.Equal(JobResultStatus.Success, res.Status);
            Assert.Equal(2, res.FilesCopied);
            Assert.Equal(2, res.FileResults.Count);
        }

        // F. Stop/recoverable interruption
        [Fact]
        public async Task TestF_StopRecoverableInterruption_CheckpointRemainsValid()
        {
            var checkpointsDir = Path.Combine(_testRootDir, "cp_f");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Interrupted Job",
                CurrentState = ExecutionState.Stopped,
                TotalFiles = 5,
                CompletedFiles = 2,
                PendingFiles = 3,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var retrieved = await checkpointRepo.GetCheckpointAsync(jobId);
            Assert.NotNull(retrieved);
            Assert.True(retrieved.IsRecoverable);
            Assert.Equal(ExecutionState.Stopped, retrieved.CurrentState);
        }

        // G. Pause -> shutdown -> restart
        [Fact]
        public async Task TestG_PauseShutdownRestart_RecoveryCardDiscoveredNoAutoExecution()
        {
            var checkpointsDir = Path.Combine(_testRootDir, "cp_g");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Paused Job",
                CurrentState = ExecutionState.Paused,
                TotalFiles = 10,
                CompletedFiles = 4,
                PendingFiles = 6,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);

            // Startup check (QuartzScheduled trigger) must be REJECTED
            var resScheduled = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.QuartzScheduled);
            Assert.False(resScheduled.Allowed);

            // Startup check (ManualRun trigger) must be REJECTED
            var resManual = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.ManualRun);
            Assert.False(resManual.Allowed);

            // RecoveryResume trigger must be ALLOWED
            var resRecovery = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume);
            Assert.True(resRecovery.Allowed);
        }

        // H. Explicit RecoveryResume
        [Fact]
        public async Task TestH_ExplicitRecoveryResume_AllowedByGate()
        {
            var checkpointsDir = Path.Combine(_testRootDir, "cp_h");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                CurrentState = ExecutionState.Cancelled,
                TotalFiles = 5,
                CompletedFiles = 1,
                PendingFiles = 4,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var gate = new JobExecutionGate(checkpointRepo);
            var gateRes = await gate.CanExecuteAsync(jobId, ExecutionTriggerSource.RecoveryResume);

            Assert.True(gateRes.Allowed);
        }

        // I. Retry failed historical file
        [Fact]
        public async Task TestI_RetryFailedHistoricalFile_OnlyFailedOrIncompleteScopeSelected()
        {
            var srcDir = Path.Combine(_testRootDir, "retry_src");
            var destDir = Path.Combine(_testRootDir, "retry_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "file1.txt");
            string file2 = Path.Combine(srcDir, "file2.txt");
            await File.WriteAllTextAsync(file1, "Data 1");
            await File.WriteAllTextAsync(file2, "Data 2");

            var failedItems = new List<FileItemResult>
            {
                new FileItemResult { SourcePath = file1, DestinationPath = Path.Combine(destDir, "file1.txt"), FileName = "file1.txt", FileSize = 6, Status = FileItemStatus.Completed },
                new FileItemResult { SourcePath = file2, DestinationPath = Path.Combine(destDir, "file2.txt"), FileName = "file2.txt", FileSize = 6, Status = FileItemStatus.Incomplete }
            };

            var service = new FileCopyService(new DummyLogService());
            var job = new Job { Id = Guid.NewGuid(), Name = "Retry Job", SourcePaths = new List<string> { file1, file2 }, DestinationPath = destDir };

            var res = await service.RetryFailedFilesAsync(job, failedItems, false, null, CancellationToken.None);

            // Only file2 (Incomplete) should have been retried
            Assert.Equal(1, res.FilesCopied);
            Assert.Equal(FileItemStatus.Completed, failedItems[1].Status);
        }

        // J. Retry while unresolved checkpoint exists
        [Fact]
        public async Task TestJ_RetryWhileUnresolvedCheckpointExists_GatePreventsExecution()
        {
            var checkpointsDir = Path.Combine(_testRootDir, "cp_j");
            var checkpointRepo = new CheckpointRepository(customDirectory: checkpointsDir);
            var jobId = Guid.NewGuid();

            var cp = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Active Job",
                CurrentState = ExecutionState.Running,
                IsRecoverable = true
            };
            await checkpointRepo.SaveCheckpointAsync(cp);

            var dialogService = new DummyDialogService();
            var historyRepo = new HistoryRepository(customDataDir: Path.Combine(_testRootDir, "hist_j"));
            var jobRepo = new DummyJobRepository(new[] { new Job { Id = jobId, Name = "Active Job" } });
            var gate = new JobExecutionGate(checkpointRepo);

            var entry = new HistoryEntry
            {
                JobId = jobId,
                JobName = "Active Job",
                FilesFailed = 1,
                FileResults = new List<FileItemResult>
                {
                    new FileItemResult { SourcePath = "c:\\src.txt", DestinationPath = "c:\\dest.txt", FileName = "src.txt", Status = FileItemStatus.Failed }
                }
            };

            var jobExecutionManager = new JobExecutionManager();
            var gateWithManager = new JobExecutionGate(checkpointRepo, null, jobExecutionManager);
            jobExecutionManager.RegisterSession(new Job { Id = jobId, Name = "Active Job" });

            var vm = new HistoryDetailViewModel(entry, jobRepo, new FileCopyService(), historyRepo, dialogService, gateWithManager, checkpointRepo);

            await vm.RetryAllFailedAsync();

            // Dialog should show message warning user that execution is blocked
            Assert.Equal("İşlem Engellendi", dialogService.MessageTitle);
        }

        // K. Legacy history entry without file details
        [Fact]
        public async Task TestK_LegacyHistoryEntry_LoadsSuccessfullyAndShowsSafeEmptyState()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Legacy Job",
                FilesCopied = 10,
                BytesCopied = 1024 * 1024,
                Status = JobResultStatus.Success,
                FileResults = null! // Legacy entry without file details
            };

            var dialogService = new DummyDialogService();
            var historyRepo = new HistoryRepository(customDataDir: Path.Combine(_testRootDir, "hist_k"));
            var jobRepo = new DummyJobRepository(new Job[0]);

            var vm = new HistoryDetailViewModel(entry, jobRepo, new FileCopyService(), historyRepo, dialogService);

            Assert.True(vm.HasNoFileDetails);
            Assert.Empty(vm.DisplayFileResults);
        }

        // L. Repeated refresh
        [Fact]
        public async Task TestL_RepeatedRefresh_NoDuplicateHistoryEntriesOrRows()
        {
            var historyRepo = new HistoryRepository(customDataDir: Path.Combine(_testRootDir, "hist_l"));
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Unique Job",
                StartTime = DateTime.Now,
                Status = JobResultStatus.Success
            };
            await historyRepo.AddAsync(entry);

            var dialogService = new DummyDialogService();
            var logService = new DummyLogService();
            var vm = new HistoryViewModel(historyRepo, dialogService, logService);

            await vm.LoadHistoryAsync();
            Assert.Single(vm.HistoryEntries);

            await vm.LoadHistoryAsync();
            Assert.Single(vm.HistoryEntries);

            await vm.LoadHistoryAsync();
            Assert.Single(vm.HistoryEntries);
        }
    }
}
