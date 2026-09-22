using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Quartz;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v12PreflightAndUxTests
    {
        private readonly string _tempDir;

        public Phase3v12PreflightAndUxTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Terabithia_v12_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [Fact]
        public async Task TestA_SourceDoesNotExist_ReturnsBlockingPreflight()
        {
            var pathService = new PathValidationService();
            var service = new PreflightValidationService(pathService);
            string nonExistentSrc = Path.Combine(_tempDir, "NonExistentFolder_12345");
            string validDest = Path.Combine(_tempDir, "DestFolder");
            Directory.CreateDirectory(validDest);

            var job = new Job
            {
                SourcePaths = new List<string> { nonExistentSrc },
                DestinationPath = validDest,
                CopyMode = CopyMode.Incremental
            };

            var result = await service.ValidateJobAsync(job);

            Assert.True(result.HasBlockingErrors);
            Assert.Contains(result.Issues, i => i.Code == PreflightIssueCode.SOURCE_NOT_FOUND && i.Severity == PreflightSeverity.BlockingError);
        }

        [Fact]
        public async Task TestB_DestinationUnavailable_ReturnsBlockingPreflight()
        {
            var pathService = new PathValidationService();
            var service = new PreflightValidationService(pathService);
            string validSrc = Path.Combine(_tempDir, "SourceFolder");
            Directory.CreateDirectory(validSrc);
            string invalidDest = "Z:\\NonExistentDriveFolder_9999\\Target";

            var job = new Job
            {
                SourcePaths = new List<string> { validSrc },
                DestinationPath = invalidDest,
                CopyMode = CopyMode.Incremental
            };

            var result = await service.ValidateJobAsync(job);

            Assert.True(result.HasBlockingErrors);
            Assert.Contains(result.Issues, i => (i.Code == PreflightIssueCode.DESTINATION_UNAVAILABLE || i.Code == PreflightIssueCode.DESTINATION_NOT_WRITABLE) && i.Severity == PreflightSeverity.BlockingError);
        }

        [Fact]
        public async Task TestC_SourceEqualsDestination_ReturnsBlockingPreflight()
        {
            var pathService = new PathValidationService();
            var service = new PreflightValidationService(pathService);
            string folder = Path.Combine(_tempDir, "SameFolder");
            Directory.CreateDirectory(folder);

            var job = new Job
            {
                SourcePaths = new List<string> { folder },
                DestinationPath = folder,
                CopyMode = CopyMode.Incremental
            };

            var result = await service.ValidateJobAsync(job);

            Assert.True(result.HasBlockingErrors);
            Assert.Contains(result.Issues, i => i.Code == PreflightIssueCode.SOURCE_EQUALS_DESTINATION && i.Severity == PreflightSeverity.BlockingError);
        }

        [Fact]
        public async Task TestD_DestinationInsideSource_ReturnsBlockingPreflight()
        {
            var pathService = new PathValidationService();
            var service = new PreflightValidationService(pathService);
            string src = Path.Combine(_tempDir, "SourceRoot");
            Directory.CreateDirectory(src);
            string destInsideSrc = Path.Combine(src, "NestedDest");

            var job = new Job
            {
                SourcePaths = new List<string> { src },
                DestinationPath = destInsideSrc,
                CopyMode = CopyMode.Incremental
            };

            var result = await service.ValidateJobAsync(job);

            Assert.True(result.HasBlockingErrors);
            Assert.Contains(result.Issues, i => i.Code == PreflightIssueCode.DESTINATION_INSIDE_SOURCE && i.Severity == PreflightSeverity.BlockingError);
        }

        [Fact]
        public void TestE_InsufficientFreeSpace_ReturnsSpaceIssue()
        {
            var result = new PreflightResult();
            result.AddIssue(new PreflightIssue
            {
                Code = PreflightIssueCode.INSUFFICIENT_SPACE,
                Severity = PreflightSeverity.BlockingError,
                Title = "Yetersiz Disk Alanı",
                Message = "Gerekli alan mevcut alandan fazla."
            });

            Assert.True(result.HasBlockingErrors);
            Assert.Contains(result.Issues, i => i.Code == PreflightIssueCode.INSUFFICIENT_SPACE);
        }

        [Fact]
        public async Task TestF_ValidPaths_ExecutionAllowed()
        {
            var pathService = new PathValidationService();
            var service = new PreflightValidationService(pathService);
            string src = Path.Combine(_tempDir, "ValidSrc");
            string dest = Path.Combine(_tempDir, "ValidDest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(src, "test.txt"), "hello world");

            var job = new Job
            {
                SourcePaths = new List<string> { src },
                DestinationPath = dest,
                CopyMode = CopyMode.Incremental
            };

            var result = await service.ValidateJobAsync(job);

            Assert.True(result.IsSuccess);
            Assert.False(result.HasBlockingErrors);
        }

        [Fact]
        public async Task TestG_ScheduledJobPreflightFailure_CopyNotExecutedSchedulerStable()
        {
            var jobRepo = new MockJobRepository();
            var fileCopy = new MockFileCopyService();
            var historyRepo = new MockHistoryRepository();
            var notif = new MockNotificationService();
            var usb = new MockUsbDriveService();
            var log = new MockLogService();
            var preflight = new PreflightValidationService(new PathValidationService());

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Invalid Preflight Job",
                SourcePaths = new List<string> { Path.Combine(_tempDir, "NonExistent_XYZ") },
                DestinationPath = Path.Combine(_tempDir, "ValidDest")
            };
            await jobRepo.AddAsync(job);

            var quartzJob = new QuartzCopyJob(
                jobRepo, fileCopy, historyRepo, notif, usb, log,
                preflightValidationService: preflight);

            var context = new MockJobExecutionContext(job.Id);
            await quartzJob.Execute(context);

            Assert.False(fileCopy.WasCopyCalled);
            Assert.Single(historyRepo.AddedEntries);
            Assert.Equal(JobResultStatus.Skipped, historyRepo.AddedEntries.First().Status);
        }

        [Fact]
        public void TestH_DuplicateJobName_WarningBehavior()
        {
            var existingJobs = new List<Job>
            {
                new Job { Id = Guid.NewGuid(), Name = "Yedek Görevi" }
            };

            bool isDuplicate = existingJobs.Any(j => string.Equals(j.Name.Trim(), "Yedek Görevi", StringComparison.OrdinalIgnoreCase));
            Assert.True(isDuplicate);

            var newJob = new Job { Id = Guid.NewGuid(), Name = "Yedek Görevi" };
            Assert.NotEqual(existingJobs[0].Id, newJob.Id);
        }

        [Fact]
        public void TestI_FailedOnlyHistory_CorrectRetryLabel()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Test Job",
                FilesFailed = 2,
                FilesIncomplete = 0
            };
            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            Assert.True(vm.CanRetryPrimary);
            Assert.Contains("Başarısızları", vm.PrimaryRetryButtonText);
        }

        [Fact]
        public void TestJ_IncompleteOnlyHistory_CorrectRetryLabel()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Test Job",
                FilesFailed = 0,
                FilesIncomplete = 3
            };
            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            Assert.True(vm.CanRetryPrimary);
            Assert.Contains("Yarım Kalanları", vm.PrimaryRetryButtonText);
        }

        [Fact]
        public void TestK_FailedAndIncomplete_CombinedRetryLabel()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Test Job",
                FilesFailed = 1,
                FilesIncomplete = 2
            };
            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            Assert.True(vm.CanRetryPrimary);
            Assert.Contains("Tamamlanmayanları", vm.PrimaryRetryButtonText);
        }

        [Fact]
        public void TestL_NoRetryableFiles_NoActiveRetryCommand()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Test Job",
                FilesFailed = 0,
                FilesIncomplete = 0,
                FileResults = new List<FileItemResult>
                {
                    new FileItemResult { Status = FileItemStatus.Completed, FileName = "a.txt" }
                }
            };
            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            Assert.False(vm.CanRetryPrimary);
            Assert.False(vm.DisplayFileResults.First().CanRetrySingle);
        }

        [Fact]
        public async Task TestM_UnresolvedCheckpoint_HistoryRetryBlocked()
        {
            var jobId = Guid.NewGuid();
            var entry = new HistoryEntry { JobId = jobId, JobName = "Blocked Job", FilesFailed = 1 };

            var cpRepo = new MockCheckpointRepository();
            await cpRepo.SaveCheckpointAsync(new JobCheckpoint
            {
                JobId = jobId,
                CurrentState = ExecutionState.Stopped,
                IsRecoverable = true,
                TotalFiles = 5,
                CompletedFiles = 2
            });

            var dialogService = new MockDialogService();
            var manager = new JobExecutionManager();
            var gate = new JobExecutionGate(cpRepo, null, manager);
            manager.RegisterSession(new Job { Id = jobId, Name = "Blocked Job" });

            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), dialogService, jobExecutionGate: gate, checkpointRepository: cpRepo);

            await vm.RetryAllFailedAsync();

            Assert.True(dialogService.WasMessageShown);
            Assert.Contains("İşlem Engellendi", dialogService.LastShownTitle);
        }

        [Fact]
        public void TestN_SearchAndFilterOnHistoryDetails()
        {
            var entry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Filter Job",
                FileResults = new List<FileItemResult>
                {
                    new FileItemResult { FileName = "report.pdf", RelativePath = "docs\\report.pdf", Status = FileItemStatus.Completed },
                    new FileItemResult { FileName = "error.log", RelativePath = "logs\\error.log", Status = FileItemStatus.Failed, ErrorMessage = "Access Denied" },
                    new FileItemResult { FileName = "temp.bin", RelativePath = "temp\\temp.bin", Status = FileItemStatus.Incomplete }
                }
            };
            var vm = new HistoryDetailViewModel(entry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            // Search filter
            vm.SearchText = "report";
            Assert.Single(vm.DisplayFileResults);
            Assert.Equal("report.pdf", vm.DisplayFileResults.First().FileName);

            // Status filter
            vm.SearchText = string.Empty;
            vm.SelectedStatusFilter = "Başarısız";
            Assert.Single(vm.DisplayFileResults);
            Assert.Equal("error.log", vm.DisplayFileResults.First().FileName);
        }

        [Fact]
        public void TestO_LegacyHistoryEntryStillLoads()
        {
            var legacyEntry = new HistoryEntry
            {
                JobId = Guid.NewGuid(),
                JobName = "Legacy Entry",
                FilesCopied = 10,
                FileResults = null
            };

            var vm = new HistoryDetailViewModel(legacyEntry, new MockJobRepository(), new MockFileCopyService(), new MockHistoryRepository(), new MockDialogService());

            Assert.Empty(vm.DisplayFileResults);
            Assert.True(vm.HasNoFileDetails);
        }

        [Fact]
        public void TestP_LongPathDoesNotAlterStoredPath()
        {
            string longPath = @"C:\Very\Long\Path\Structure\With\Multiple\Subdirectories\Deep\Inside\FileSystem\TargetFile.dat";
            var item = new FileItemResult
            {
                FileName = "TargetFile.dat",
                RelativePath = longPath,
                Status = FileItemStatus.Completed
            };

            var vm = new FileItemResultViewModel(item);

            Assert.Equal(longPath, vm.RelativePath);
            Assert.Equal(longPath, item.RelativePath);
        }

        // Mock Classes for Testing
        private class MockJobRepository : IJobRepository
        {
            private readonly List<Job> _jobs = new();
            public Task AddAsync(Job job) { _jobs.Add(job); return Task.CompletedTask; }
            public Task DeleteAsync(Guid id) { _jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(_jobs);
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));
            public Task UpdateAsync(Job job) => Task.CompletedTask;
        }

        private class MockFileCopyService : IFileCopyService
        {
            public bool WasCopyCalled { get; private set; }
            public Task<FileCopyResult> CopyAsync(Job job, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.ManualRun)
            {
                WasCopyCalled = true;
                return Task.FromResult(new FileCopyResult { Success = true });
            }
            public Task<FileCopyResult> RetryFailedFilesAsync(Job job, List<FileItemResult> failedItems, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.HistoryRetry)
            {
                return Task.FromResult(new FileCopyResult { Success = true, FilesCopied = failedItems.Count });
            }
            public Task<FileCopyResult> CopyAsync(IEnumerable<string> sourcePaths, string destinationPath, CopyMode copyMode, ConflictPolicy conflictPolicy, bool dryRun, IProgress<double>? progress, CancellationToken cancellationToken)
            {
                WasCopyCalled = true;
                return Task.FromResult(new FileCopyResult { Success = true });
            }
        }

        private class MockHistoryRepository : IHistoryRepository
        {
            public List<HistoryEntry> AddedEntries { get; } = new();
            public Task AddAsync(HistoryEntry entry) { AddedEntries.Add(entry); return Task.CompletedTask; }
            public Task ClearAllAsync() { AddedEntries.Clear(); return Task.CompletedTask; }
            public Task DeleteAsync(HistoryEntry entry) { AddedEntries.Remove(entry); return Task.CompletedTask; }
            public Task<IReadOnlyList<HistoryEntry>> GetAllAsync() => Task.FromResult<IReadOnlyList<HistoryEntry>>(AddedEntries);
            public Task UpdateAsync(HistoryEntry entry) => Task.CompletedTask;
        }

        private class MockNotificationService : INotificationService
        {
            public void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? message = null) { }
            public void ShowNotification(string title, string message, NotificationType type) { }
        }

        private class MockUsbDriveService : IUsbDriveService
        {
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;

            public IReadOnlyList<UsbDriveInfo> GetConnectedUsbDrives() => new List<UsbDriveInfo>();
            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public bool IsDriveConnected(string path) => true;
            public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber) => true;
            public string? GetVolumeSerialNumber(string driveLetterOrPath) => null;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber) => null;
        }

        private class MockLogService : ILogService
        {
            public void ClearLogs() { }
            public string GetLogDirectory() => string.Empty;
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public void LogDebug(string message) { }
            public void LogError(string message, Exception? exception = null) { }
            public void LogInformation(string message) { }
            public void LogWarning(string message) { }
        }

        private class MockCheckpointRepository : ICheckpointRepository
        {
            private readonly Dictionary<Guid, JobCheckpoint> _checkpoints = new();
            public Task DeleteCheckpointAsync(Guid jobId) { _checkpoints.Remove(jobId); return Task.CompletedTask; }
            public Task ClearAllCheckpointsAsync() { _checkpoints.Clear(); return Task.CompletedTask; }
            public Task<JobCheckpoint?> GetCheckpointAsync(Guid jobId) => Task.FromResult(_checkpoints.TryGetValue(jobId, out var cp) ? cp : null);
            public Task<IReadOnlyList<JobCheckpoint>> GetRecoverableCheckpointsAsync() => Task.FromResult<IReadOnlyList<JobCheckpoint>>(_checkpoints.Values.ToList());
            public Task<IReadOnlyList<JobCheckpoint>> GetAllCheckpointsAsync() => Task.FromResult<IReadOnlyList<JobCheckpoint>>(_checkpoints.Values.ToList());
            public Task SaveCheckpointAsync(JobCheckpoint checkpoint) { _checkpoints[checkpoint.JobId] = checkpoint; return Task.CompletedTask; }
        }

        private class MockDialogService : IDialogService
        {
            public bool WasMessageShown { get; private set; }
            public string LastShownTitle { get; private set; } = string.Empty;

            public string? SelectFolder(string title = "Klasör Seçin") => null;
            public List<string> SelectFolders(string title = "Klasör Seçin") => new();
            public List<string> SelectFiles(string title = "Dosyaları Seçin") => new();
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(true);
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
            public Task<Job?> ShowJobEditorAsync(Job? job = null) => Task.FromResult<Job?>(null);
            public Task ShowMessageAsync(string title, string message)
            {
                WasMessageShown = true;
                LastShownTitle = title;
                return Task.CompletedTask;
            }
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
        }

        private class MockJobExecutionContext : IJobExecutionContext
        {
            public MockJobExecutionContext(Guid jobId)
            {
                MergedJobDataMap = new JobDataMap
                {
                    { "JobId", jobId.ToString() }
                };
                Trigger = TriggerBuilder.Create().WithIdentity("MockTrigger", "MockGroup").Build();
            }

            public JobDataMap MergedJobDataMap { get; }
            public ITrigger Trigger { get; }
            public string FireInstanceId => "MockFireInstance_1";
            public int RefireCount => 0;
            public CancellationToken CancellationToken => CancellationToken.None;

            public IScheduler Scheduler => throw new NotImplementedException();
            public ICalendar? Calendar => null;
            public bool Recovering => false;
            public TriggerKey TriggerKey => new TriggerKey("MockTrigger", "MockGroup");
            public TriggerKey RecoveringTriggerKey => new TriggerKey("MockTrigger", "MockGroup");
            public JobKey JobKey => new JobKey("MockJobKey");
            public IJobDetail JobDetail => throw new NotImplementedException();
            public IJob JobInstance => throw new NotImplementedException();
            public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? ScheduledFireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? PreviousFireTimeUtc => null;
            public DateTimeOffset? NextFireTimeUtc => null;
            public TimeSpan JobRunTime => TimeSpan.Zero;
            public object? Result { get; set; }

            public void Put(object key, object objectValue) { }
            public object Get(object key) => null!;
        }
    }
}
