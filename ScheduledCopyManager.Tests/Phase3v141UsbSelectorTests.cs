using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v141UsbSelectorTests
    {
        private class DummyPathValidationService : IPathValidationService
        {
            public Task<IReadOnlyList<string>> ValidateSourcesAsync(IEnumerable<string> sourcePaths) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateDestinationAsync(string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateJobPathsAsync(IEnumerable<string> sourcePaths, string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
        }

        private class MockDialogService : IDialogService
        {
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task ShowLogDetailsAsync(LogEntry logEntry) => Task.CompletedTask;
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

        private class DummyLogService : ILogService
        {
            private readonly List<LogEntry> _logs = new();
            public void ClearLogs() => _logs.Clear();
            public void LogError(string message, Exception? exception = null) => _logs.Add(new LogEntry { Level = "ERROR", Message = message });
            public void LogInformation(string message) => _logs.Add(new LogEntry { Level = "INFO", Message = message });
            public void LogWarning(string message) => _logs.Add(new LogEntry { Level = "WARN", Message = message });
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => _logs.AsReadOnly();
            public string GetLogDirectory() => Path.Combine(Path.GetTempPath(), "Logs");
        }

        [Fact]
        public void RequirementA_UsbServiceReturnsRemovableDrive_JobEditorContainsDrive()
        {
            // Arrange
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                AvailableFreeSpace = 50L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService);

            // Act
            vm.IsUsbDestination = true;

            // Assert
            Assert.Single(vm.AvailableUsbDrives);
            Assert.Equal("E88A-1D3C", vm.AvailableUsbDrives[0].VolumeSerialNumber);
            Assert.Equal(@"E:\", vm.AvailableUsbDrives[0].DriveLetter);
        }

        [Fact]
        public void RequirementB_SelectDriveE_DestinationPathBecomesRootE()
        {
            // Arrange
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                AvailableFreeSpace = 50L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService);

            vm.IsUsbDestination = true;

            // Assert
            Assert.Equal(@"E:\", vm.DestinationPath);
            Assert.Equal("E88A-1D3C", vm.TargetVolumeSerialNumber);
        }

        [Fact]
        public void RequirementC_SelectDriveGAfterwards_DestinationPathBecomesRootG()
        {
            // Arrange
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü 1",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                IsReady = true
            });
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"G:\",
                VolumeLabel = "USB Sürücü 2",
                VolumeSerialNumber = "G77B-2E4D",
                TotalSize = 32L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService);

            vm.IsUsbDestination = true;
            Assert.Equal(@"E:\", vm.DestinationPath);

            // Act - Select G drive option
            var gDrive = vm.AvailableUsbDrives.FirstOrDefault(d => d.DriveLetter.StartsWith("G"));
            Assert.NotNull(gDrive);
            vm.SelectedUsbDrive = gDrive;

            // Assert
            Assert.Equal(@"G:\", vm.DestinationPath);
            Assert.Equal("G77B-2E4D", vm.TargetVolumeSerialNumber);
        }

        [Fact]
        public void RequirementD_UserEditsDestinationSubfolder_RefreshDoesNotResetIt()
        {
            // Arrange
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService);

            vm.IsUsbDestination = true;
            Assert.Equal(@"E:\", vm.DestinationPath);

            // User edits destination path to a custom subfolder
            vm.DestinationPath = @"E:\TerabithiaBackup";

            // Act - Refresh drives
            vm.RefreshUsbDrives();

            // Assert - Subfolder text is preserved
            Assert.Equal(@"E:\TerabithiaBackup", vm.DestinationPath);
        }

        [Fact]
        public void RequirementE_DriveLetterRemapped_SerialIdentityPreservedAndRootUpdated()
        {
            // Arrange - USB drive reconnected on G:\ instead of E:\ with same serial E88A-1D3C
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"G:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var existingJob = new Job
            {
                Name = "Remapped USB Job",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "E88A-1D3C",
                TargetVolumeLabel = "USB Sürücü",
                DestinationPath = @"E:\OldPath\Backups"
            };

            // Act
            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                existingJob: existingJob,
                usbDriveService: usbService);

            // Assert - Serial identity preserved, destination path re-rooted to G:\OldPath\Backups
            Assert.Equal("E88A-1D3C", vm.TargetVolumeSerialNumber);
            Assert.Equal(@"G:\OldPath\Backups", vm.DestinationPath);
        }

        [Fact]
        public void RequirementF_NoUsbPresent_ClearEmptyStateWithoutFakePath()
        {
            // Arrange - No connected USB drives
            var usbService = new MockUsbDriveService();

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService);

            // Act
            vm.IsUsbDestination = true;

            // Assert
            Assert.Empty(vm.AvailableUsbDrives);
            Assert.Null(vm.SelectedUsbDrive);
            Assert.True(vm.HasUsbStatusMessage);
            Assert.Equal("Bağlı USB sürücüsü bulunamadı.", vm.UsbStatusMessage);
            Assert.Equal(string.Empty, vm.DestinationPath);
        }

        [Fact]
        public void RequirementG_DisconnectedSavedUsb_SerialPreservedAndPlaceholderCreated()
        {
            // Arrange - USB drive A1B2-C3D4 disconnected, another drive X9Y8-Z7W6 connected
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"H:\",
                VolumeLabel = "Other Flash",
                VolumeSerialNumber = "X9Y8-Z7W6",
                TotalSize = 16L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var existingJob = new Job
            {
                Name = "Disconnected USB Job",
                IsUsbDestination = true,
                TargetVolumeSerialNumber = "A1B2-C3D4",
                TargetVolumeLabel = "My Offsite USB",
                DestinationPath = @"K:\SavedFolder"
            };

            // Act
            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                existingJob: existingJob,
                usbDriveService: usbService);

            // Assert
            Assert.Equal("A1B2-C3D4", vm.TargetVolumeSerialNumber);
            Assert.NotNull(vm.SelectedUsbDrive);
            Assert.False(vm.SelectedUsbDrive.IsPresent);
            Assert.Equal("A1B2-C3D4", vm.SelectedUsbDrive.VolumeSerialNumber);
            Assert.Equal(@"K:\SavedFolder", vm.DestinationPath);
            Assert.True(vm.HasUsbStatusMessage);
            Assert.Contains("bağlı değil", vm.UsbStatusMessage);
        }

        [Fact]
        public void RequirementH_ToolsViewAndJobEditorReceiveSameDriveService()
        {
            // Arrange
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                IsReady = true
            });

            var logService = new DummyLogService();
            var dialogService = new DialogService(new DummyPathValidationService(), usbDriveService: usbService, logService: logService);

            var toolsVm = new ToolsViewModel(logService, usbService);
            toolsVm.Initialize();

            // Act
            var editorVm = new JobEditorViewModel(
                new DummyPathValidationService(),
                dialogService,
                usbDriveService: usbService,
                logService: logService);
            editorVm.IsUsbDestination = true;

            // Assert
            Assert.True(toolsVm.HasRemovableDrives);
            Assert.Single(toolsVm.RemovableDrives);
            Assert.Equal(@"E:\", toolsVm.RemovableDrives[0].DriveLetter);

            Assert.NotEmpty(editorVm.AvailableUsbDrives);
            Assert.Equal(@"E:\", editorVm.AvailableUsbDrives[0].DriveLetter);
            Assert.Equal(@"E:\", editorVm.DestinationPath);
        }

        [Fact]
        public void RequirementI_VentoyMultiVolume_LargeDataVolumeSelectedAsDefaultOverTinyHelper()
        {
            // Arrange - E:\ 57.6 GB exFAT data partition, F:\ 31.7 MB FAT helper partition
            var usbService = new MockUsbDriveService();
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"E:\",
                VolumeLabel = "USB Sürücü",
                VolumeSerialNumber = "E88A-1D3C",
                TotalSize = 57L * 1024 * 1024 * 1024,
                IsReady = true
            });
            usbService.ConnectedDrives.Add(new UsbDriveInfo
            {
                Name = @"F:\",
                VolumeLabel = "VTOYEFI",
                VolumeSerialNumber = "EA6C-95B2",
                TotalSize = 31L * 1024 * 1024, // 31.7 MB
                IsReady = true
            });

            var logService = new DummyLogService();

            var vm = new JobEditorViewModel(
                new DummyPathValidationService(),
                new MockDialogService(),
                usbDriveService: usbService,
                logService: logService);

            // Act
            vm.IsUsbDestination = true;

            // Assert
            Assert.Equal(2, vm.AvailableUsbDrives.Count);
            Assert.NotNull(vm.SelectedUsbDrive);
            Assert.Equal(@"E:\", vm.SelectedUsbDrive.DriveLetter);
            Assert.Equal("E88A-1D3C", vm.SelectedUsbDrive.VolumeSerialNumber);
            Assert.Equal(@"E:\", vm.DestinationPath);
        }
    }
}
