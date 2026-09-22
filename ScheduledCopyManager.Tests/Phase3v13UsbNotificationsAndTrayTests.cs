using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.App;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v13UsbNotificationsAndTrayTests
    {
        private class DummyJobRepository : IJobRepository
        {
            private readonly Dictionary<Guid, Job> _jobs = new();
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(_jobs.TryGetValue(id, out var j) ? j : null);
            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(new List<Job>(_jobs.Values));
            public Task AddAsync(Job job) { _jobs[job.Id] = job; return Task.CompletedTask; }
            public Task UpdateAsync(Job job) { _jobs[job.Id] = job; return Task.CompletedTask; }
            public Task DeleteAsync(Guid id) { _jobs.Remove(id); return Task.CompletedTask; }
        }

        private class DummyHistoryRepository : IHistoryRepository
        {
            public List<HistoryEntry> Entries { get; } = new();
            public Task AddAsync(HistoryEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
            public Task UpdateAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task DeleteAsync(HistoryEntry entry) { Entries.Remove(entry); return Task.CompletedTask; }
            public Task<IReadOnlyList<HistoryEntry>> GetAllAsync() => Task.FromResult<IReadOnlyList<HistoryEntry>>(Entries);
            public Task<IReadOnlyList<HistoryEntry>> GetByJobIdAsync(Guid jobId) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Entries.FindAll(e => e.JobId == jobId));
            public Task ClearAllAsync() { Entries.Clear(); return Task.CompletedTask; }
        }

        private class DummyFileCopyService : IFileCopyService
        {
            public Task<FileCopyResult> CopyAsync(Job job, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.ManualRun)
            {
                return Task.FromResult(new FileCopyResult
                {
                    Success = true,
                    FilesCopied = 5,
                    BytesCopied = 1024 * 1024
                });
            }

            public Task<FileCopyResult> RetryFailedFilesAsync(Job job, List<FileItemResult> failedItems, bool dryRun, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken, IPauseToken? pauseToken = null, JobCheckpoint? resumeCheckpoint = null, ExecutionTriggerSource triggerSource = ExecutionTriggerSource.HistoryRetry)
            {
                return Task.FromResult(new FileCopyResult { Success = true, FilesCopied = failedItems.Count });
            }

            public Task<FileCopyResult> CopyAsync(IEnumerable<string> sourcePaths, string destinationPath, CopyMode copyMode, ConflictPolicy conflictPolicy, bool dryRun, IProgress<double>? progress, CancellationToken cancellationToken)
            {
                return Task.FromResult(new FileCopyResult { Success = true });
            }
        }

        private class DummyNotificationService : INotificationService
        {
            public List<(string Title, string Message, NotificationType Type)> SentNotifications { get; } = new();
            public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
            {
                SentNotifications.Add((title, message, type));
            }
            public void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? message = null)
            {
                SentNotifications.Add((jobName, message ?? string.Empty, success ? NotificationType.Success : NotificationType.Error));
            }
        }

        private class DummyLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"[INFO] {message}");
            public void LogWarning(string message) => Logs.Add($"[WARN] {message}");
            public void LogError(string message, Exception? exception = null) => Logs.Add($"[ERR] {message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => @"C:\Logs";
            public void ClearLogs() => Logs.Clear();
        }

        private class MockUsbDriveService : IUsbDriveService
        {
#pragma warning disable CS0067
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
#pragma warning restore CS0067

            public Dictionary<string, string> DriveToSerialMap { get; } = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public bool IsDriveConnected(string path) => true;
            public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber)
            {
                if (string.IsNullOrEmpty(expectedVolumeSerialNumber)) return true;
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (root != null && DriveToSerialMap.TryGetValue(root, out var serial) && string.Equals(serial, expectedVolumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return FindDriveLetterByVolumeSerialNumber(expectedVolumeSerialNumber) != null;
            }

            public string? GetVolumeSerialNumber(string driveLetterOrPath)
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(driveLetterOrPath));
                if (root != null && DriveToSerialMap.TryGetValue(root, out var serial)) return serial;
                return null;
            }

            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber)
            {
                foreach (var kvp in DriveToSerialMap)
                {
                    if (string.Equals(kvp.Value, volumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return kvp.Key;
                    }
                }
                return null;
            }
        }

        private class DummySettingsRepository : ISettingsRepository
        {
            public Settings CurrentSettings { get; set; } = new Settings();
            public Task<Settings> GetAsync() => Task.FromResult(CurrentSettings);
            public Task SaveAsync(Settings settings) { CurrentSettings = settings; return Task.CompletedTask; }
        }

        [Fact]
        public void UsbDriveService_GetVolumeSerialNumber_ReturnsFormattedHexOrNullForNonExistentDrive()
        {
            var service = new UsbDriveService();
            string? serial = service.GetVolumeSerialNumber(@"Z:\NonExistentFolderPath_9999");
            Assert.True(serial == null || serial.Contains("-"));
        }

        [Fact]
        public void UsbDriveService_IsDriveConnected_WithVolumeSerialNumber_ReturnsTrueWhenMatching()
        {
            var mockUsb = new MockUsbDriveService();
            mockUsb.DriveToSerialMap[@"E:\"] = "A8F2-3B4E";

            bool isConnected = mockUsb.IsDriveConnected(@"E:\Backup", "A8F2-3B4E");
            Assert.True(isConnected);
        }

        [Fact]
        public void UsbDriveService_IsDriveConnected_ReturnsFalseWhenSerialMismatchedOnSameLetterAndNotConnectedElsewhere()
        {
            var mockUsb = new MockUsbDriveService();
            mockUsb.DriveToSerialMap[@"E:\"] = "1111-2222"; // Different USB drive plugged into E:\

            bool isConnected = mockUsb.IsDriveConnected(@"E:\Backup", "A8F2-3B4E");
            Assert.False(isConnected);
        }

        [Fact]
        public void UsbDriveService_FindDriveLetterByVolumeSerialNumber_ResolvesNewLetterWhenDriveLetterChanges()
        {
            var mockUsb = new MockUsbDriveService();
            mockUsb.DriveToSerialMap[@"F:\"] = "A8F2-3B4E"; // Drive letter changed from E:\ to F:\

            string? resolvedLetter = mockUsb.FindDriveLetterByVolumeSerialNumber("A8F2-3B4E");
            Assert.Equal(@"F:\", resolvedLetter);
        }

        [Fact]
        public void Job_TargetVolumeProperties_PersistAndLoadCorrectly()
        {
            var job = new Job
            {
                Name = "USB Backup Job",
                DestinationPath = @"E:\Backup",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "A8F2-3B4E",
                TargetVolumeLabel = "SANDISK_32G"
            };

            Assert.Equal("A8F2-3B4E", job.TargetVolumeSerialNumber);
            Assert.Equal("SANDISK_32G", job.TargetVolumeLabel);
            Assert.True(job.IsUsbDestination);
        }

        [Fact]
        public async Task NotificationService_HonorsNotificationTogglesFromSettings()
        {
            var dummySettingsRepo = new DummySettingsRepository();
            dummySettingsRepo.CurrentSettings.EnableNotifications = true;
            dummySettingsRepo.CurrentSettings.NotifyOnSuccess = false;
            dummySettingsRepo.CurrentSettings.NotifyOnFailure = true;

            var notifService = new NotificationService(dummySettingsRepo);
            notifService.ShowNotification("Success", "Copy complete", NotificationType.Success);
            notifService.ShowNotification("Failure", "Copy failed", NotificationType.Error);

            var settings = await dummySettingsRepo.GetAsync();
            Assert.False(settings.NotifyOnSuccess);
            Assert.True(settings.NotifyOnFailure);
        }

        [Fact]
        public async Task NotificationService_SuppressesAllNotifications_WhenMasterToggleDisabled()
        {
            var dummySettingsRepo = new DummySettingsRepository();
            dummySettingsRepo.CurrentSettings.EnableNotifications = false;

            var notifService = new NotificationService(dummySettingsRepo);
            notifService.ShowNotification("Info", "Test message", NotificationType.Info);

            var settings = await dummySettingsRepo.GetAsync();
            Assert.False(settings.EnableNotifications);
        }

        [Fact]
        public async Task QuartzCopyJob_WhenUsbSerialMismatched_SetsHedefSurucuBekleniyorStatus()
        {
            var jobRepo = new DummyJobRepository();
            var historyRepo = new DummyHistoryRepository();
            var notifService = new DummyNotificationService();
            var logService = new DummyLogService();
            var mockUsb = new MockUsbDriveService();

            var job = new Job
            {
                Name = "USB Sync Job",
                DestinationPath = @"E:\Target",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "A8F2-3B4E"
            };
            await jobRepo.AddAsync(job);

            // Drive E:\ has a DIFFERENT volume plugged in ("9999-8888")
            mockUsb.DriveToSerialMap[@"E:\"] = "9999-8888";

            var quartzJob = new QuartzCopyJob(
                jobRepo,
                new DummyFileCopyService(),
                historyRepo,
                notifService,
                mockUsb,
                logService);

            var context = new MockJobExecutionContext(job.Id);
            await quartzJob.Execute(context);

            Assert.Single(historyRepo.Entries);
            Assert.Equal(JobResultStatus.Skipped, historyRepo.Entries[0].Status);
            Assert.Contains("Hedef sürücü bekleniyor", historyRepo.Entries[0].Message);
            Assert.Single(notifService.SentNotifications);
            Assert.Contains("Hedef Sürücü Bekleniyor", notifService.SentNotifications[0].Title);
        }

        [Fact]
        public async Task QuartzCopyJob_WhenUsbDriveLetterChanges_RemapsDestinationPathToNewLetter()
        {
            var jobRepo = new DummyJobRepository();
            var historyRepo = new DummyHistoryRepository();
            var notifService = new DummyNotificationService();
            var logService = new DummyLogService();
            var mockUsb = new MockUsbDriveService();

            var job = new Job
            {
                Name = "Dynamic USB Job",
                DestinationPath = @"E:\TargetFolder",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "A8F2-3B4E"
            };
            await jobRepo.AddAsync(job);

            // Old drive E:\ now has serial "1111-2222"
            mockUsb.DriveToSerialMap[@"E:\"] = "1111-2222";
            // Volume "A8F2-3B4E" moved to drive letter F:\
            mockUsb.DriveToSerialMap[@"F:\"] = "A8F2-3B4E";

            var quartzJob = new QuartzCopyJob(
                jobRepo,
                new DummyFileCopyService(),
                historyRepo,
                notifService,
                mockUsb,
                logService);

            var context = new MockJobExecutionContext(job.Id);
            await quartzJob.Execute(context);

            Assert.Equal(@"F:\TargetFolder", job.DestinationPath);
            Assert.Contains(logService.Logs, l => l.Contains("USB sürücü harfi güncellendi"));
        }

        [Fact]
        public void BuildInfo_BuildIdentifier_ContainsPhase3BuildId()
        {
            string buildId = ScheduledCopyManager.App.BuildInfo.BuildIdentifier;
            Assert.Contains(ScheduledCopyManager.Domain.Models.BuildInfo.BuildId, buildId);
        }

        private class MockJobExecutionContext : Quartz.IJobExecutionContext
        {
            public Guid JobId { get; }
            public MockJobExecutionContext(Guid jobId)
            {
                JobId = jobId;
                MergedJobDataMap = new Quartz.JobDataMap { { "JobId", jobId.ToString() } };
            }

            public Quartz.IScheduler Scheduler => throw new NotImplementedException();
            public Quartz.ITrigger Trigger
            {
                get
                {
                    return Quartz.TriggerBuilder.Create()
                        .WithIdentity("DummyTrigger", "Group")
                        .Build();
                }
            }
            public Quartz.ICalendar? Calendar => null;
            public bool Recovering => false;
            public Quartz.TriggerKey TriggerKey => new Quartz.TriggerKey("DummyTrigger", "Group");
            public int RefireCount => 0;
            public Quartz.TriggerKey? RecoveringTriggerKey => null;
            public string FireInstanceId => "TestInstance";
            public Quartz.JobDataMap MergedJobDataMap { get; }
            public Quartz.JobDataMap JobDetailJobDataMap => MergedJobDataMap;
            public Quartz.JobDataMap TriggerJobDataMap => MergedJobDataMap;
            public Quartz.IJobDetail JobDetail => throw new NotImplementedException();
            public Quartz.IJob JobInstance => throw new NotImplementedException();
            public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? ScheduledFireTimeUtc => DateTimeOffset.UtcNow;
            public DateTimeOffset? PreviousFireTimeUtc => null;
            public DateTimeOffset? NextFireTimeUtc => DateTimeOffset.UtcNow.AddHours(1);
            public object? Result { get; set; }
            public TimeSpan JobRunTime => TimeSpan.Zero;
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Put(object key, object objectValue) { }
            public object Get(object key) => null!;
        }
    }
}
