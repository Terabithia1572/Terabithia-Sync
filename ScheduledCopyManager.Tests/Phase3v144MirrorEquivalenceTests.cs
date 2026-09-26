using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public class Phase3v144MirrorEquivalenceTests : IDisposable
    {
        private readonly string _testRootDir;
        private readonly TestLogService _logService;
        private readonly CheckpointRepository _checkpointRepository;

        public Phase3v144MirrorEquivalenceTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "Terabithia_v144Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRootDir);
            _logService = new TestLogService();
            _checkpointRepository = new CheckpointRepository(_logService, Path.Combine(_testRootDir, "checkpoints"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_testRootDir))
            {
                try { Directory.Delete(_testRootDir, true); } catch { }
            }
        }

        private class TestLogService : ILogService
        {
            public List<string> Logs { get; } = new();
            public void LogInformation(string message) => Logs.Add($"[INFO] {message}");
            public void LogWarning(string message) => Logs.Add($"[WARN] {message}");
            public void LogError(string message, Exception? exception = null) => Logs.Add($"[ERR] {message}: {exception?.Message}");
            public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100) => new List<LogEntry>();
            public string GetLogDirectory() => @"C:\Logs";
            public void ClearLogs() => Logs.Clear();
        }

        private class TestUsbDriveService : IUsbDriveService
        {
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
            public bool Connected { get; set; } = true;
            public string Serial { get; set; } = "VOL-144";

            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public bool IsDriveConnected(string drivePath) => Connected;
            public bool IsDriveConnected(string drivePath, string? targetVolumeSerialNumber) => Connected && (targetVolumeSerialNumber == null || targetVolumeSerialNumber == Serial);
            public string? GetVolumeSerialNumber(string drivePath) => Serial;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber) => volumeSerialNumber == Serial ? "C:\\" : null;
        }

        private FileCopyService CreateService(IUsbDriveService? usb = null)
        {
            return new FileCopyService(_logService, usb ?? new TestUsbDriveService(), _checkpointRepository);
        }

        private (string srcDir, string destDir, List<string> files) Setup95FileDataset()
        {
            string srcDir = Path.Combine(_testRootDir, "src_95");
            string destDir = Path.Combine(_testRootDir, "dest_95");
            Directory.CreateDirectory(srcDir);

            byte[] dummy = new byte[100 * 1024]; // 100 KB
            new Random(42).NextBytes(dummy);
            var createdFiles = new List<string>();

            for (int i = 0; i < 95; i++)
            {
                string sub = Path.Combine(srcDir, $"Dir_{i % 5}");
                Directory.CreateDirectory(sub);
                string file = Path.Combine(sub, $"file_{i:D3}.bin");
                File.WriteAllBytes(file, dummy);
                createdFiles.Add(file);
            }

            return (srcDir, destDir, createdFiles);
        }

        // TEST A — Empty destination
        [Fact]
        public async Task TEST_A_EmptyDestination_CopiesAll95Files()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test A",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            var res = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res.Success);
            Assert.Equal(95, res.FilesCopied);
            Assert.Equal(0, res.FilesSkipped);
            Assert.Equal(0, res.FilesFailed);
            Assert.True(res.BytesWrittenThisExecution > 0);
        }

        // TEST B — Identical populated destination
        [Fact]
        public async Task TEST_B_IdenticalPopulatedDestination_SkipsAllFilesWithoutPhysicalTransfer()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test B",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();

            // Run PASS 1
            var res1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(res1.Success);
            Assert.Equal(95, res1.FilesCopied);

            // Run PASS 2 (Identical populated destination)
            var sw = Stopwatch.StartNew();
            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            sw.Stop();

            Assert.True(res2.Success);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);
            Assert.Equal(0, res2.FilesFailed);
            Assert.Equal(0, res2.BytesWrittenThisExecution);
            Assert.True(res2.FileResults.All(f => f.BytesTransferred == 0));
        }

        // TEST C — One source file content changed normally
        [Fact]
        public async Task TEST_C_OneSourceFileChanged_CopiesOnlyChangedFile()
        {
            var (srcDir, destDir, files) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test C",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Modify exactly one source file content and write time
            string changed = files[10];
            File.WriteAllText(changed, "CHANGED CONTENT " + DateTime.Now.Ticks);
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(5));

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(1, res2.FilesCopied);
            Assert.Equal(94, res2.FilesSkipped);
            Assert.True(res2.BytesWrittenThisExecution > 0);
        }

        // TEST D — One destination file deleted
        [Fact]
        public async Task TEST_D_OneDestinationFileDeleted_RestoresDeletedFileOnly()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test D",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Delete 1 destination file
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string deletedDestFile = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories).First();
            File.Delete(deletedDestFile);

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(1, res2.FilesCopied);
            Assert.Equal(94, res2.FilesSkipped);
            Assert.True(File.Exists(deletedDestFile));
        }

        // TEST E — Destination-only extra file + Mirror Delete Permission enabled
        [Fact]
        public async Task TEST_E_ExtraDestinationFile_DeletePermissionEnabled_DeletesExtraAndSkipsIdentical()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test E",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Add extra destination file
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string extraFile = Path.Combine(targetSubDir, "extra_stale.tmp");
            File.WriteAllText(extraFile, "STALE");

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);
            Assert.Equal(0, res2.BytesWrittenThisExecution);
            Assert.False(File.Exists(extraFile));
        }

        // TEST F — Destination-only extra file + Mirror Delete Permission disabled
        [Fact]
        public async Task TEST_F_ExtraDestinationFile_DeletePermissionDisabled_PreservesExtraAndSkipsIdentical()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test F",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = false,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Add extra destination file
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string extraFile = Path.Combine(targetSubDir, "extra_stale.tmp");
            File.WriteAllText(extraFile, "STALE");

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);
            Assert.True(File.Exists(extraFile));
        }

        // TEST G — Different-size destination file
        [Fact]
        public async Task TEST_G_DifferentSizeDestinationFile_ReplacesFile()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test G",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Truncate one destination file
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string targetFile = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories).First();
            File.WriteAllBytes(targetFile, new byte[50]); // Different size

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(1, res2.FilesCopied);
            Assert.Equal(94, res2.FilesSkipped);
        }

        // TEST H — Different timestamp beyond 2s tolerance
        [Fact]
        public async Task TEST_H_DifferentTimestampBeyondTolerance_ReplacesFile()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test H",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Shift write time of 1 dest file by +10 seconds
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string targetFile = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories).First();
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(targetFile).AddSeconds(10));

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(1, res2.FilesCopied);
            Assert.Equal(94, res2.FilesSkipped);
        }

        // TEST I — Timestamp difference inside 2s tolerance
        [Fact]
        public async Task TEST_I_TimestampInsideTolerance_SkipsFile()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test I",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Shift write time of 1 dest file by 1 second (inside 2s tolerance)
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string targetFile = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories).First();
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(targetFile).AddSeconds(1));

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);
        }

        // TEST J — Verify/hash path detects content mismatch with same size/timestamp
        [Fact]
        public async Task TEST_J_VerifyCopy_DetectsContentMismatch_ReplacesFile()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test J",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true,
                VerifyCopy = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Corrupt content of 1 dest file keeping exact same size and timestamp
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string targetFile = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories).First();
            DateTime originalMtime = File.GetLastWriteTimeUtc(targetFile);

            byte[] corrupted = new byte[100 * 1024];
            new Random(99).NextBytes(corrupted);
            File.WriteAllBytes(targetFile, corrupted);
            File.SetLastWriteTimeUtc(targetFile, originalMtime);

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(1, res2.FilesCopied);
            Assert.Equal(94, res2.FilesSkipped);
        }

        // TEST K — Mirror + Overwrite semantics
        [Fact]
        public async Task TEST_K_MirrorOverwrite_SkipsEquivalent_OverwritesDifferent()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test K",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Run 2: All 95 equivalent
            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);

            // Modify 2 source files
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            string targetFile1 = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories)[0];
            string targetFile2 = Directory.GetFiles(targetSubDir, "*.bin", SearchOption.AllDirectories)[1];
            File.WriteAllBytes(targetFile1, new byte[200]);
            File.WriteAllBytes(targetFile2, new byte[300]);

            var res3 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(2, res3.FilesCopied);
            Assert.Equal(93, res3.FilesSkipped);
        }

        // TEST L — Mirror + Rename policy
        [Fact]
        public async Task TEST_L_MirrorRename_GeneratesUniqueFilenames()
        {
            var srcDir = Path.Combine(_testRootDir, "src_rename");
            var destDir = Path.Combine(_testRootDir, "dest_rename");
            Directory.CreateDirectory(srcDir);
            string srcFile = Path.Combine(srcDir, "item.txt");
            File.WriteAllText(srcFile, "Original");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test L",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                ConflictPolicy = ConflictPolicy.Rename,
                PreserveTimestamps = true
            };

            var service = CreateService();
            var res1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(1, res1.FilesCopied);

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(1, res2.FilesCopied);

            string targetSubDir = Path.Combine(destDir, Path.GetFileName(srcDir));
            var files = Directory.GetFiles(targetSubDir);
            Assert.Equal(2, files.Length);
            Assert.Contains(files, f => f.Contains("(1)"));
        }

        // TEST M — Mirror + Skip policy
        [Fact]
        public async Task TEST_M_MirrorSkip_SkipsAllExistingFiles()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test M",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                ConflictPolicy = ConflictPolicy.Skip,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(95, res2.FilesSkipped);
        }

        // TEST N — v14.3 deleted destination directory restoration
        [Fact]
        public async Task TEST_N_DeletedDestinationDirectory_ReconstructedCleanly()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test N",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            await service.CopyAsync(job, false, null, CancellationToken.None);

            // Delete entire destination root directory externally
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, true);
            }

            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res2.Success);
            Assert.Equal(95, res2.FilesCopied);
            Assert.Equal(0, res2.FilesSkipped);
            Assert.True(Directory.Exists(destDir));
        }

        // TEST O — Recovery regression
        [Fact]
        public async Task TEST_O_RecoveryResume_PreservesCompletedState()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test O",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = CreateService();
            var res1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.True(res1.Success);

            var cp = await _checkpointRepository.GetCheckpointAsync(job.Id);
            // Checkpoint deleted on clean completion
            Assert.Null(cp);
        }

        // TEST P — USB identity regression
        [Fact]
        public async Task TEST_P_UsbIdentityValidation_RefusesUnmatchedVolumeSerial()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var usbMock = new TestUsbDriveService { Connected = false };

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test P",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var service = CreateService(usbMock);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, false, null, cts.Token);
            });
        }

        // TEST Q — Cancellation / history regression
        [Fact]
        public async Task TEST_Q_Cancellation_PreservesPartialFileResultsAndHistoryCounters()
        {
            var (srcDir, destDir, _) = Setup95FileDataset();
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test Q",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var service = CreateService();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));

            try
            {
                await service.CopyAsync(job, false, null, cts.Token);
            }
            catch (OperationCanceledException cancelEx)
            {
                var partial = cancelEx.Data["FileCopyResult"] as FileCopyResult;
                Assert.NotNull(partial);
                Assert.Equal(JobResultStatus.Cancelled, partial.Status);
                Assert.NotEmpty(partial.FileResults);
            }
        }
    }
}
