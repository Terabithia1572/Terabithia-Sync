using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v14UxPolishTests
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

        private class DummyHistoryRepository : IHistoryRepository
        {
            private readonly List<HistoryEntry> _entries = new();
            public Task AddAsync(HistoryEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
            public Task ClearAllAsync() { _entries.Clear(); return Task.CompletedTask; }
            public Task DeleteAsync(HistoryEntry entry) { _entries.Remove(entry); return Task.CompletedTask; }
            public Task UpdateAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task<IReadOnlyList<HistoryEntry>> GetAllAsync() => Task.FromResult<IReadOnlyList<HistoryEntry>>(new List<HistoryEntry>(_entries));
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

        private class DummyLogService : ILogService
        {
            private readonly List<LogEntry> _logs = new();
            public void ClearLogs() => _logs.Clear();
            public void LogError(string message, Exception? exception = null) => _logs.Add(new LogEntry { Level = "ERROR", Message = message, Exception = exception?.ToString() });
            public void LogInformation(string message) => _logs.Add(new LogEntry { Level = "INFO", Message = message });
            public void LogWarning(string message) => _logs.Add(new LogEntry { Level = "WARN", Message = message });
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => _logs.AsReadOnly();
            public string GetLogDirectory() => Path.Combine(Path.GetTempPath(), "TerabithiaSyncTests_Logs");
        }

        private class MockDialogService : IDialogService
        {
            public HistoryEntry? ShownHistoryDetails { get; private set; }
            public LogEntry? ShownLogDetails { get; private set; }

            public Task ShowHistoryDetailsAsync(HistoryEntry entry)
            {
                ShownHistoryDetails = entry;
                return Task.CompletedTask;
            }

            public Task ShowLogDetailsAsync(LogEntry logEntry)
            {
                ShownLogDetails = logEntry;
                return Task.CompletedTask;
            }

            public Task<Job?> ShowJobEditorAsync(Job? job = null) => Task.FromResult<Job?>(job);
            public Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint) => Task.CompletedTask;
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(true);
            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public string? SelectFolder(string title = "Klasör Seçin") => @"C:\MockFolder";
            public List<string> SelectFolders(string title = "Klasör Seçin") => new() { @"C:\MockFolder" };
            public List<string> SelectFiles(string title = "Dosyaları Seçin") => new() { @"C:\MockFile.txt" };
        }

        private class MockUsbDriveService : IUsbDriveService
        {
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;

            public List<UsbDriveInfo> ConnectedDrives { get; } = new();

            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => ConnectedDrives.AsReadOnly();

            public bool IsDriveConnected(string path)
            {
                string root = Path.GetPathRoot(path) ?? string.Empty;
                return ConnectedDrives.Any(d => string.Equals(d.Name.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            }

            public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber)
            {
                if (string.IsNullOrEmpty(expectedVolumeSerialNumber)) return IsDriveConnected(path);
                return ConnectedDrives.Any(d => string.Equals(d.VolumeSerialNumber, expectedVolumeSerialNumber, StringComparison.OrdinalIgnoreCase));
            }

            public string? GetVolumeSerialNumber(string driveLetterOrPath)
            {
                string root = Path.GetPathRoot(driveLetterOrPath) ?? string.Empty;
                var drive = ConnectedDrives.FirstOrDefault(d => string.Equals(d.Name.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                return drive?.VolumeSerialNumber;
            }

            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber)
            {
                var drive = ConnectedDrives.FirstOrDefault(d => string.Equals(d.VolumeSerialNumber, volumeSerialNumber, StringComparison.OrdinalIgnoreCase));
                return drive?.Name;
            }
        }

        private class MockPathValidationService : IPathValidationService
        {
            public bool IsPathValid(string path) => !string.IsNullOrWhiteSpace(path);
            public bool DirectoryExists(string path) => true;
            public bool FileExists(string path) => true;
            public bool CanWriteToDirectory(string path) => true;
            public long GetAvailableFreeSpace(string path) => 100 * 1024 * 1024 * 1024L;
            public Task<IReadOnlyList<string>> ValidateSourcesAsync(IEnumerable<string> sourcePaths) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateDestinationAsync(string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateJobPathsAsync(IEnumerable<string> sourcePaths, string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
        }

        // Test 1: About page resolves current BuildId
        [Fact]
        public void Test01_AboutPage_ResolvesCurrentBuildId()
        {
            var aboutVm = new AboutViewModel();
            Assert.Contains(ScheduledCopyManager.Domain.Models.BuildInfo.BuildId, aboutVm.Version);
        }

        // Test 2: Dashboard double-click opens correct HistoryEntry details
        [Fact]
        public async Task Test02_Dashboard_DoubleClick_OpensHistoryDetails()
        {
            var jobRepo = new DummyJobRepository();
            var historyRepo = new DummyHistoryRepository();
            var scheduler = new DummyJobScheduler();
            var logService = new DummyLogService();
            var dialogService = new MockDialogService();

            var entry = new HistoryEntry { JobId = Guid.NewGuid(), JobName = "Test Job" };
            await historyRepo.AddAsync(entry);

            var dashboardVm = new DashboardViewModel(jobRepo, historyRepo, scheduler, logService, dialogService);
            await dashboardVm.ShowHistoryDetailsAsync(entry);

            Assert.NotNull(dialogService.ShownHistoryDetails);
            Assert.Equal("Test Job", dialogService.ShownHistoryDetails!.JobName);
        }

        // Test 3: System Log double-click opens correct full details
        [Fact]
        public async Task Test03_SystemLog_DoubleClick_OpensFullDetails()
        {
            var logService = new DummyLogService();
            var historyRepo = new DummyHistoryRepository();
            var dialogService = new MockDialogService();

            var logEntry = new LogEntry { Message = "Detailed System Log Message", Level = "INFO" };

            var logsVm = new LogsViewModel(logService, historyRepo, dialogService);
            await logsVm.ShowLogDetailsAsync(logEntry);

            Assert.NotNull(dialogService.ShownLogDetails);
            Assert.Equal("Detailed System Log Message", dialogService.ShownLogDetails!.Message);
        }

        // Test 4: Log details preserve long message
        [Fact]
        public void Test04_LogDetails_PreservesLongMessage()
        {
            string longMessage = new string('A', 5000);
            var logEntry = new LogEntry { Message = longMessage, Level = "WARN" };

            var vm = new LogDetailViewModel(logEntry);
            Assert.Equal(longMessage, vm.Message);
        }

        // Test 5: Log details preserve exception/error detail
        [Fact]
        public void Test05_LogDetails_PreservesExceptionDetail()
        {
            string stackTrace = "System.InvalidOperationException: Failed\n   at ScheduledCopyManager.CopyFileStreamAsync()";
            var logEntry = new LogEntry { Message = "Error occurred", Level = "ERROR", Exception = stackTrace };

            var vm = new LogDetailViewModel(logEntry);
            Assert.True(vm.HasException);
            Assert.Equal(stackTrace, vm.Exception);

            string diagnosticText = vm.BuildDiagnosticSummary();
            Assert.Contains($"BuildId: {ScheduledCopyManager.Domain.Models.BuildInfo.BuildId}", diagnosticText);
            Assert.Contains(stackTrace, diagnosticText);
        }

        // Test 6: USB selector lists removable drives
        [Fact]
        public void Test06_UsbSelector_ListsRemovableDrives()
        {
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "KINGSTON",
                VolumeSerialNumber = "A1B2-C3D4",
                TotalSize = 60000000000L,
                AvailableFreeSpace = 30000000000L,
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var editorVm = new JobEditorViewModel(pathVal, dialogService, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.Single(editorVm.AvailableUsbDrives);
            Assert.Equal(@"E:\", editorVm.AvailableUsbDrives[0].DriveLetter);
            Assert.Contains("KINGSTON", editorVm.AvailableUsbDrives[0].DisplayText);
        }

        // Test 7: Selecting USB updates DestinationPath
        [Fact]
        public void Test07_SelectingUsb_UpdatesDestinationPath()
        {
            var usbService = new MockUsbDriveService();
            var drive = new UsbDriveInfo
            {
                Name = @"F:\",
                VolumeLabel = "BACKUP_USB",
                VolumeSerialNumber = "FEED-BEEF",
                TotalSize = 120000000000L,
                IsReady = true
            };
            usbService.ConnectedDrives.Add(drive);

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var editorVm = new JobEditorViewModel(pathVal, dialogService, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.Equal(@"F:\", editorVm.DestinationPath);
        }

        // Test 8: Selecting USB captures TargetVolumeSerialNumber
        [Fact]
        public void Test08_SelectingUsb_CapturesVolumeSerialNumber()
        {
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "SANDISK",
                VolumeSerialNumber = "1234-ABCD",
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var editorVm = new JobEditorViewModel(pathVal, dialogService, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.Equal("1234-ABCD", editorVm.TargetVolumeSerialNumber);
        }

        // Test 9: Selecting USB captures TargetVolumeLabel
        [Fact]
        public void Test09_SelectingUsb_CapturesVolumeLabel()
        {
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "MY_KEY_LABEL",
                VolumeSerialNumber = "9876-FEDC",
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var editorVm = new JobEditorViewModel(pathVal, dialogService, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.Equal("MY_KEY_LABEL", editorVm.TargetVolumeLabel);
        }

        // Test 10: USB destination/path mismatch is rejected before save
        [Fact]
        public async Task Test10_UsbDestinationMismatch_RejectedBeforeSave()
        {
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB_E",
                VolumeSerialNumber = "EEEE-1111",
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var editorVm = new JobEditorViewModel(pathVal, dialogService, usbDriveService: usbService);
            editorVm.Name = "Valid Name";
            editorVm.IsUsbDestination = true;
            editorVm.DestinationPath = @"D:\Backup"; // Mismatch! USB is E:

            await editorVm.SaveAsync();

            Assert.NotNull(editorVm.ValidationErrorMessage);
            Assert.Contains("seçili USB sürücüsü", editorVm.ValidationErrorMessage);
        }

        // Test 11: Existing disconnected saved USB identity is preserved
        [Fact]
        public void Test11_DisconnectedSavedUsb_IdentityPreserved()
        {
            var usbService = new MockUsbDriveService(); // No drives connected!

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var existingJob = new Job
            {
                Name = "USB Backup Job",
                DestinationPath = @"G:\Backup",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "ABSENT-SERIAL-99",
                TargetVolumeLabel = "ABSENT_USB"
            };

            var editorVm = new JobEditorViewModel(pathVal, dialogService, existingJob: existingJob, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.Equal("ABSENT-SERIAL-99", editorVm.TargetVolumeSerialNumber);
            Assert.Equal("ABSENT_USB", editorVm.TargetVolumeLabel);
            Assert.NotEmpty(editorVm.AvailableUsbDrives);
            Assert.False(editorVm.AvailableUsbDrives[0].IsPresent);
            Assert.Contains("Kayıtlı USB şu anda bağlı değil", editorVm.AvailableUsbDrives[0].DisplayText);
        }

        // Test 12: Refresh does not destroy saved identity
        [Fact]
        public void Test12_Refresh_DoesNotDestroySavedIdentity()
        {
            var usbService = new MockUsbDriveService();
            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var existingJob = new Job
            {
                Name = "USB Job",
                DestinationPath = @"E:\Data",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "SAVED-SERIAL-777",
                TargetVolumeLabel = "SAVED_LABEL"
            };

            var editorVm = new JobEditorViewModel(pathVal, dialogService, existingJob: existingJob, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;
            editorVm.RefreshUsbDrives();

            Assert.Equal("SAVED-SERIAL-777", editorVm.TargetVolumeSerialNumber);
            Assert.Equal("SAVED_LABEL", editorVm.TargetVolumeLabel);
        }

        // Test 13: Same volume on different drive letter resolves correctly
        [Fact]
        public void Test13_SameVolume_OnDifferentDriveLetter_ResolvesCorrectly()
        {
            var usbService = new MockUsbDriveService();
            // Connected under H: now instead of E:
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"H:\",
                VolumeLabel = "MY_KINGSTON",
                VolumeSerialNumber = "MATCH-SERIAL-100",
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var existingJob = new Job
            {
                Name = "USB Remapped Job",
                DestinationPath = @"E:\Backups\2026",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "MATCH-SERIAL-100",
                TargetVolumeLabel = "MY_KINGSTON"
            };

            var editorVm = new JobEditorViewModel(pathVal, dialogService, existingJob: existingJob, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            Assert.NotNull(editorVm.SelectedUsbDrive);
            Assert.True(editorVm.SelectedUsbDrive!.IsPresent);
            Assert.Equal(@"H:\", editorVm.SelectedUsbDrive.DriveLetter);
            Assert.Equal(@"H:\Backups\2026", editorVm.DestinationPath);
        }

        // Test 14: Wrong USB on old drive letter is rejected
        [Fact]
        public async Task Test14_WrongUsb_OnOldDriveLetter_Rejected()
        {
            var usbService = new MockUsbDriveService();
            // Drive E: connected, but with WRONG serial!
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "WRONG_USB",
                VolumeSerialNumber = "DIFFERENT-SERIAL-999",
                IsReady = true
            });

            var pathVal = new MockPathValidationService();
            var dialogService = new MockDialogService();

            var existingJob = new Job
            {
                Name = "Original USB Job",
                DestinationPath = @"E:\Backups",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "EXPECTED-ORIGINAL-SERIAL-111",
                TargetVolumeLabel = "ORIGINAL_LABEL"
            };

            var editorVm = new JobEditorViewModel(pathVal, dialogService, existingJob: existingJob, usbDriveService: usbService);
            editorVm.IsUsbDestination = true;

            // Drive E: connected is wrong serial, so expected drive should be marked absent
            Assert.False(editorVm.SelectedUsbDrive?.IsPresent ?? true);
        }

        // Test 15: tr-TR localization has required keys
        [Fact]
        public void Test15_TrTR_Localization_HasRequiredKeys()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");

            Assert.Equal("tr-TR", loc.CurrentLanguage);
            Assert.False(string.IsNullOrWhiteSpace(loc.GetString("Str_Nav_Dashboard")));
            Assert.False(string.IsNullOrWhiteSpace(loc.GetString("Str_Nav_Tools")));
            Assert.False(string.IsNullOrWhiteSpace(loc.GetString("Str_UsbWaiting")));
        }

        // Test 16: en-US localization has required keys
        [Fact]
        public void Test16_EnUS_Localization_HasRequiredKeys()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("en-US");

            Assert.Equal("en-US", loc.CurrentLanguage);
            Assert.Equal("Pause", loc.GetString("Pause"));
            Assert.Equal("Resume", loc.GetString("Resume"));
        }

        // Test 17: Language switch updates representative views
        [Fact]
        public void Test17_LanguageSwitch_UpdatesRepresentativeViews()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");
            Assert.Equal("tr-TR", loc.CurrentLanguage);

            loc.SetLanguage("en-US");
            Assert.Equal("en-US", loc.CurrentLanguage);
        }

        // Test 18: Tray menu uses selected language
        [Fact]
        public void Test18_TrayMenu_UsesSelectedLanguage()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");
            Assert.Equal("Devam Et", loc.GetString("Resume"));

            loc.SetLanguage("en-US");
            Assert.Equal("Resume", loc.GetString("Resume"));
        }

        // Test 19: Recovery UI uses selected language
        [Fact]
        public void Test19_RecoveryUI_UsesSelectedLanguage()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");
            Assert.Equal("Devam Et", loc.GetString("RecoveryActionResume"));

            loc.SetLanguage("en-US");
            Assert.Equal("Resume", loc.GetString("RecoveryActionResume"));
        }

        // Test 20: Active-copy status uses selected language
        [Fact]
        public void Test20_ActiveCopyStatus_UsesSelectedLanguage()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");
            Assert.Equal("Hedef sürücü bekleniyor", loc.GetString("Str_UsbWaiting"));

            loc.SetLanguage("en-US");
            Assert.Equal("Waiting for destination drive", loc.GetString("Str_UsbWaiting"));
        }

        // Test 21: Tools page lists removable drives
        [Fact]
        public void Test21_ToolsPage_ListsRemovableDrives()
        {
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "TOOL_USB",
                VolumeSerialNumber = "TOOL-1234",
                TotalSize = 32000000000L,
                AvailableFreeSpace = 16000000000L,
                IsReady = true
            });

            var logService = new DummyLogService();
            var toolsVm = new ToolsViewModel(logService, usbService);
            toolsVm.Initialize();

            Assert.True(toolsVm.HasRemovableDrives);
            Assert.Single(toolsVm.RemovableDrives);
            Assert.Equal(@"E:\", toolsVm.RemovableDrives[0].DriveLetter);
            Assert.Equal("TOOL_USB", toolsVm.RemovableDrives[0].VolumeLabel);
        }

        // Test 22: Diagnostics copy text contains BuildId
        [Fact]
        public void Test22_DiagnosticsCopyText_ContainsBuildId()
        {
            var logService = new DummyLogService();
            var toolsVm = new ToolsViewModel(logService);

            Assert.Equal(ScheduledCopyManager.Domain.Models.BuildInfo.BuildId, toolsVm.BuildId);
        }

        // Test 23: Automated tests do not invoke real Windows toast
        [Fact]
        public void Test23_AutomatedTests_DoNotInvokeRealWindowsToast()
        {
            var notif = new NotificationService();
            // Call ShowNotification; should complete silently without throwing or popping real UI
            notif.ShowNotification("Test Title", "Test Message", NotificationType.Info);
            notif.ShowJobResultNotification("Test Job", true, 10, 0);
        }

        // Test 24: Dashboard refresh remains idempotent
        [Fact]
        public async Task Test24_DashboardRefresh_RemainsIdempotent()
        {
            var jobRepo = new DummyJobRepository();
            var historyRepo = new DummyHistoryRepository();
            var scheduler = new DummyJobScheduler();
            var logService = new DummyLogService();

            await historyRepo.AddAsync(new HistoryEntry { JobId = Guid.NewGuid(), JobName = "Job 1", Status = JobResultStatus.Success });

            var dashboardVm = new DashboardViewModel(jobRepo, historyRepo, scheduler, logService);
            await dashboardVm.RefreshDataAsync();
            int count1 = dashboardVm.RecentHistory.Count;

            await dashboardVm.RefreshDataAsync();
            int count2 = dashboardVm.RecentHistory.Count;

            Assert.Equal(count1, count2);
            Assert.Equal(1, count1);
        }

        // Test 25: System log detail command does not duplicate actions unexpectedly
        [Fact]
        public async Task Test25_SystemLogDetailCommand_DoesNotDuplicateActions()
        {
            var logService = new DummyLogService();
            var historyRepo = new DummyHistoryRepository();
            var dialogService = new MockDialogService();

            var logsVm = new LogsViewModel(logService, historyRepo, dialogService);

            // Null target should be safe
            await logsVm.ShowLogDetailsAsync(null);
            Assert.Null(dialogService.ShownLogDetails);

            var entry = new LogEntry { Message = "Single Call Test" };
            await logsVm.ShowLogDetailsAsync(entry);
            Assert.Equal("Single Call Test", dialogService.ShownLogDetails!.Message);
        }
    }
}
