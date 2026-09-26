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
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v143MirrorRestoreHotfixTests : IDisposable
    {
        private readonly string _testDir;
        private readonly MockLogService _logService;
        private readonly CheckpointRepository _checkpointRepository;

        public Phase3v143MirrorRestoreHotfixTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "TerabithiaSync_v143Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
            _logService = new MockLogService();
            _checkpointRepository = new CheckpointRepository(_logService, Path.Combine(_testDir, "checkpoints"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
        }

        private class MockLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"[INFO] {message}");
            public void LogWarning(string message) => Logs.Add($"[WARN] {message}");
            public void LogError(string message, Exception? exception = null) => Logs.Add($"[ERR] {message}: {exception?.Message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => @"C:\Logs";
            public void ClearLogs() => Logs.Clear();
        }

        private class MockUsbDriveService : IUsbDriveService
        {
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
            public bool IsConnected { get; set; } = true;
            public string VolumeSerial { get; set; } = "1234-5678";

            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public bool IsDriveConnected(string drivePath) => IsConnected;
            public bool IsDriveConnected(string drivePath, string? targetVolumeSerialNumber) => IsConnected;
            public string? GetVolumeSerialNumber(string drivePath) => VolumeSerial;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber) => "E:\\";
        }

        private FileCopyService CreateFileCopyService()
        {
            return new FileCopyService(
                _logService,
                new MockUsbDriveService(),
                _checkpointRepository);
        }

        [Fact]
        public async Task TEST_A_DeletedDestinationFile_RestoredOnNextExecution()
        {
            // 1. Create source file
            var srcDir = Path.Combine(_testDir, "srcA");
            var destDir = Path.Combine(_testDir, "destA");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var srcFile = Path.Combine(srcDir, "testA.txt");
            File.WriteAllText(srcFile, "Hello World Mirror A");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job A",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite
            };

            var service = CreateFileCopyService();

            // 2. Run Mirror (Execution #1)
            var result1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result1.Success);

            // 3. Assert destination file exists
            var targetFolder = Path.Combine(destDir, "srcA");
            var destFile = Path.Combine(targetFolder, "testA.txt");
            Assert.True(File.Exists(destFile));
            Assert.Equal("Hello World Mirror A", File.ReadAllText(destFile));

            // 4. Delete destination file externally
            File.Delete(destFile);
            Assert.False(File.Exists(destFile));

            // 5. Run the SAME job again as a NEW execution
            var result2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result2.Success);

            // 6. Assert destination file is recreated with correct content
            Assert.True(File.Exists(destFile));
            Assert.Equal("Hello World Mirror A", File.ReadAllText(destFile));
        }

        [Fact]
        public async Task TEST_B_DeletedDestinationDirectory_RestoredFullTreeOnNextExecution()
        {
            // 1. Source contains directory with multiple files/subdirectories
            var srcDir = Path.Combine(_testDir, "srcB");
            var destDir = Path.Combine(_testDir, "destB");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var subDir1 = Path.Combine(srcDir, "Folder1");
            var subDir2 = Path.Combine(srcDir, "Folder2", "EmptySub");
            Directory.CreateDirectory(subDir1);
            Directory.CreateDirectory(subDir2);

            File.WriteAllText(Path.Combine(srcDir, "root.txt"), "Root File");
            File.WriteAllText(Path.Combine(subDir1, "file1.txt"), "Sub File 1");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job B",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite
            };

            var service = CreateFileCopyService();

            // 2. Run Mirror
            var result1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result1.Success);

            var targetFolder = Path.Combine(destDir, "srcB");
            Assert.True(Directory.Exists(targetFolder));
            Assert.True(File.Exists(Path.Combine(targetFolder, "root.txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "Folder1", "file1.txt")));
            Assert.True(Directory.Exists(Path.Combine(targetFolder, "Folder2", "EmptySub")));

            // 3. Delete entire destination directory externally
            Directory.Delete(targetFolder, true);
            Assert.False(Directory.Exists(targetFolder));

            // 4. Run same job again
            var result2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result2.Success);

            // 5. Assert full directory tree is restored
            Assert.True(Directory.Exists(targetFolder));
            Assert.True(File.Exists(Path.Combine(targetFolder, "root.txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "Folder1", "file1.txt")));
            Assert.True(Directory.Exists(Path.Combine(targetFolder, "Folder2", "EmptySub")));
        }

        [Fact]
        public async Task TEST_C_MirrorDeletePermissionEnabled_DeletesExtraFilesAndRestoresMissingSourceFiles()
        {
            var srcDir = Path.Combine(_testDir, "srcC");
            var destDir = Path.Combine(_testDir, "destC");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            File.WriteAllText(Path.Combine(srcDir, "valid.txt"), "Valid Source");

            var targetFolder = Path.Combine(destDir, "srcC");
            Directory.CreateDirectory(targetFolder);
            File.WriteAllText(Path.Combine(targetFolder, "stale.txt"), "Stale File");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job C",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite
            };

            var service = CreateFileCopyService();
            var result = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result.Success);

            // Assert stale file deleted and missing valid file restored
            Assert.False(File.Exists(Path.Combine(targetFolder, "stale.txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "valid.txt")));
        }

        [Fact]
        public async Task TEST_D_MirrorDeletePermissionDisabled_PreservesExtraFilesAndRestoresMissingSourceFiles()
        {
            var srcDir = Path.Combine(_testDir, "srcD");
            var destDir = Path.Combine(_testDir, "destD");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            File.WriteAllText(Path.Combine(srcDir, "valid.txt"), "Valid Source");

            var targetFolder = Path.Combine(destDir, "srcD");
            Directory.CreateDirectory(targetFolder);
            File.WriteAllText(Path.Combine(targetFolder, "extra.txt"), "Extra File");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job D",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = false,
                ConflictPolicy = ConflictPolicy.Overwrite
            };

            var service = CreateFileCopyService();
            var result = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result.Success);

            // Assert extra file preserved AND missing valid file restored
            Assert.True(File.Exists(Path.Combine(targetFolder, "extra.txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "valid.txt")));
        }

        [Fact]
        public async Task TEST_E_PreviousSuccessfulHistory_DoesNotSuppressCopyingMissingDestinationFile()
        {
            var srcDir = Path.Combine(_testDir, "srcE");
            var destDir = Path.Combine(_testDir, "destE");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var srcFile = Path.Combine(srcDir, "historyTest.txt");
            File.WriteAllText(srcFile, "History Content");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job E",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var service = CreateFileCopyService();

            // Run execution #1
            var result1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result1.Success);

            var targetFolder = Path.Combine(destDir, "srcE");
            var destFile = Path.Combine(targetFolder, "historyTest.txt");
            Assert.True(File.Exists(destFile));

            // Delete destination file externally
            File.Delete(destFile);
            Assert.False(File.Exists(destFile));

            // Run execution #2
            var result2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(result2.Success);

            // Assert destination file restored despite previous success history
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task TEST_F_PreviousCompletedCheckpointState_DoesNotSuppressCopyingMissingDestinationFile()
        {
            var srcDir = Path.Combine(_testDir, "srcF");
            var destDir = Path.Combine(_testDir, "destF");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var srcFile = Path.Combine(srcDir, "cpTest.txt");
            File.WriteAllText(srcFile, "Checkpoint Content");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job F",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var targetFolder = Path.Combine(destDir, "srcF");
            var destFile = Path.Combine(targetFolder, "cpTest.txt");

            // Create a mock completed checkpoint saved on disk
            var completedCp = new JobCheckpoint
            {
                JobId = job.Id,
                JobName = job.Name,
                CurrentState = ExecutionState.Completed,
                TotalFiles = 1,
                TotalBytes = 18,
                CompletedFiles = 1,
                CompletedBytes = 18,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        SourcePath = srcFile,
                        DestinationPath = destFile,
                        SourceLength = 18,
                        Status = CheckpointFileStatus.Completed
                    }
                }
            };
            await _checkpointRepository.SaveCheckpointAsync(completedCp);

            var service = CreateFileCopyService();

            // Run new execution (resumeCheckpoint = null)
            var result = await service.CopyAsync(job, false, null, CancellationToken.None, null, null, ExecutionTriggerSource.QuartzScheduled);
            Assert.True(result.Success);
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task TEST_G_RecoveryRegression_InterruptedExecution_BehaviorIntact()
        {
            var srcDir = Path.Combine(_testDir, "srcG");
            var destDir = Path.Combine(_testDir, "destG");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var srcFile = Path.Combine(srcDir, "recoveryTest.txt");
            File.WriteAllText(srcFile, "Recovery Content");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job G",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror
            };

            var targetFolder = Path.Combine(destDir, "srcG");
            var destFile = Path.Combine(targetFolder, "recoveryTest.txt");

            // Save an unfinished recoverable checkpoint
            var interruptedCp = new JobCheckpoint
            {
                JobId = job.Id,
                JobName = job.Name,
                CurrentState = ExecutionState.Paused,
                IsRecoverable = true,
                TotalFiles = 1,
                TotalBytes = 16,
                ProcessInstanceId = Guid.NewGuid(), // Previous process session
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        SourcePath = srcFile,
                        DestinationPath = destFile,
                        SourceLength = 16,
                        Status = CheckpointFileStatus.Completed
                    }
                }
            };
            await _checkpointRepository.SaveCheckpointAsync(interruptedCp);

            var service = CreateFileCopyService();

            // Direct execution without RecoveryResume trigger source should be refused
            var refusedResult = await service.CopyAsync(job, false, null, CancellationToken.None, null, null, ExecutionTriggerSource.QuartzScheduled);
            Assert.False(refusedResult.Success);
            Assert.Equal(JobResultStatus.Cancelled, refusedResult.Status);

            // Direct execution with explicit RecoveryResume trigger source is allowed
            var allowedResult = await service.CopyAsync(job, false, null, CancellationToken.None, null, interruptedCp, ExecutionTriggerSource.RecoveryResume);
            Assert.True(allowedResult.Success);
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task TEST_H_UsbIdentityRegression_RemappingAndRejection()
        {
            var mockUsbService = new MockUsbDriveService { IsConnected = false };
            var service = new FileCopyService(_logService, mockUsbService, _checkpointRepository);

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job H",
                SourcePaths = new List<string> { _testDir },
                DestinationPath = "Z:\\Test",
                IsUsbDestination = true
            };

            // Connected check failure causes drive availability wait / error
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, false, null, cts.Token);
            });
        }

        [Fact]
        public async Task TEST_I_ScheduledVsManual_IdenticalRestorationBehavior()
        {
            var srcDir = Path.Combine(_testDir, "srcI");
            var destDir = Path.Combine(_testDir, "destI");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            var srcFile = Path.Combine(srcDir, "identicalTest.txt");
            File.WriteAllText(srcFile, "Identical Behavior Content");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job I",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var service = CreateFileCopyService();
            var targetFolder = Path.Combine(destDir, "srcI");
            var destFile = Path.Combine(targetFolder, "identicalTest.txt");

            // Test 1: Scheduled trigger source
            var resScheduled1 = await service.CopyAsync(job, false, null, CancellationToken.None, null, null, ExecutionTriggerSource.QuartzScheduled);
            Assert.True(resScheduled1.Success);
            Assert.True(File.Exists(destFile));

            File.Delete(destFile);
            Assert.False(File.Exists(destFile));

            var resScheduled2 = await service.CopyAsync(job, false, null, CancellationToken.None, null, null, ExecutionTriggerSource.QuartzScheduled);
            Assert.True(resScheduled2.Success);
            Assert.True(File.Exists(destFile));

            // Test 2: Manual run trigger source
            File.Delete(destFile);
            Assert.False(File.Exists(destFile));

            var resManual = await service.CopyAsync(job, false, null, CancellationToken.None, null, null, ExecutionTriggerSource.ManualRun);
            Assert.True(resManual.Success);
            Assert.True(File.Exists(destFile));
        }
    }
}
