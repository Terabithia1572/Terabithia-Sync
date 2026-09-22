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
    public class Phase3PersistentCheckpointAndRecoveryTests
    {
        private readonly string _testBaseDir;

        public Phase3PersistentCheckpointAndRecoveryTests()
        {
            _testBaseDir = Path.Combine(Path.GetTempPath(), "Phase3Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testBaseDir);
        }

        [Fact]
        public async Task CheckpointRepository_SavesAndLoadsCheckpointAtomically()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "checkpoints");
            var repo = new CheckpointRepository(customDirectory: checkpointsDir);

            var checkpoint = new JobCheckpoint
            {
                JobId = Guid.NewGuid(),
                JobName = "Test Job",
                SourcePaths = new List<string> { @"C:\Source" },
                DestinationPath = @"D:\Dest",
                TotalFiles = 10,
                TotalBytes = 10240,
                CompletedFiles = 5,
                CompletedBytes = 5120,
                CurrentState = ExecutionState.Paused,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "file1.txt",
                        SourcePath = @"C:\Source\file1.txt",
                        DestinationPath = @"D:\Dest\file1.txt",
                        SourceLength = 1024,
                        SourceLastWriteTimeUtc = DateTime.UtcNow,
                        Status = CheckpointFileStatus.Completed,
                        BytesCopied = 1024
                    },
                    new CheckpointFileEntry
                    {
                        RelativePath = "file2.txt",
                        SourcePath = @"C:\Source\file2.txt",
                        DestinationPath = @"D:\Dest\file2.txt",
                        SourceLength = 4096,
                        SourceLastWriteTimeUtc = DateTime.UtcNow,
                        Status = CheckpointFileStatus.Pending,
                        BytesCopied = 0
                    }
                }
            };

            await repo.SaveCheckpointAsync(checkpoint);

            string expectedPath = Path.Combine(checkpointsDir, $"{checkpoint.JobId}.checkpoint.json");
            Assert.True(File.Exists(expectedPath));

            var loaded = await repo.GetCheckpointAsync(checkpoint.JobId);
            Assert.NotNull(loaded);
            Assert.Equal(1, loaded!.SchemaVersion);
            Assert.Equal(checkpoint.JobId, loaded.JobId);
            Assert.Equal("Test Job", loaded.JobName);
            Assert.Equal(ExecutionState.Paused, loaded.CurrentState);
            Assert.Equal(2, loaded.FileEntries.Count);
            Assert.Equal(CheckpointFileStatus.Completed, loaded.FileEntries[0].Status);
            Assert.Equal(CheckpointFileStatus.Pending, loaded.FileEntries[1].Status);
        }

        [Fact]
        public async Task CheckpointRepository_CorruptCheckpoint_RenamesToCorruptAndReturnsNull()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "checkpoints");
            Directory.CreateDirectory(checkpointsDir);
            var repo = new CheckpointRepository(customDirectory: checkpointsDir);

            Guid jobId = Guid.NewGuid();
            string file = Path.Combine(checkpointsDir, $"{jobId}.checkpoint.json");
            await File.WriteAllTextAsync(file, "{ invalid json content ... }");

            var loaded = await repo.GetCheckpointAsync(jobId);
            Assert.Null(loaded);

            Assert.False(File.Exists(file));
            string corruptPath = file + ".corrupt";
            Assert.True(File.Exists(corruptPath));
        }

        [Fact]
        public async Task CheckpointRepository_GetRecoverableCheckpointsAsync_FiltersUnfinishedOnly()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "checkpoints");
            var repo = new CheckpointRepository(customDirectory: checkpointsDir);

            var unfinishedJobId = Guid.NewGuid();
            var unfinished = new JobCheckpoint
            {
                JobId = unfinishedJobId,
                JobName = "Unfinished Job",
                CurrentState = ExecutionState.Paused,
                TotalFiles = 2,
                CompletedFiles = 1,
                PendingFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry { Status = CheckpointFileStatus.Completed },
                    new CheckpointFileEntry { Status = CheckpointFileStatus.Pending }
                }
            };
            await repo.SaveCheckpointAsync(unfinished);

            var finishedJobId = Guid.NewGuid();
            var finished = new JobCheckpoint
            {
                JobId = finishedJobId,
                JobName = "Finished Job",
                CurrentState = ExecutionState.Completed,
                TotalFiles = 1,
                CompletedFiles = 1,
                PendingFiles = 0,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry { Status = CheckpointFileStatus.Completed }
                }
            };
            await repo.SaveCheckpointAsync(finished);

            var recoverable = await repo.GetRecoverableCheckpointsAsync();
            Assert.Single(recoverable);
            Assert.Equal(unfinishedJobId, recoverable[0].JobId);
        }

        [Fact]
        public async Task CheckpointRepository_DeleteCheckpointAsync_RemovesFileIfExists()
        {
            string checkpointsDir = Path.Combine(_testBaseDir, "checkpoints");
            var repo = new CheckpointRepository(customDirectory: checkpointsDir);

            var jobId = Guid.NewGuid();
            var checkpoint = new JobCheckpoint { JobId = jobId, JobName = "Delete Test" };
            await repo.SaveCheckpointAsync(checkpoint);

            Assert.NotNull(await repo.GetCheckpointAsync(jobId));

            await repo.DeleteCheckpointAsync(jobId);
            Assert.Null(await repo.GetCheckpointAsync(jobId));
        }

        [Fact]
        public async Task FileCopyService_ResumesJobAndSkipsCompletedValidatedFiles()
        {
            string srcDir = Path.Combine(_testBaseDir, "Src");
            string destDir = Path.Combine(_testBaseDir, "Dest");
            Directory.CreateDirectory(srcDir);
            string targetSubDir = Path.Combine(destDir, "Src");
            Directory.CreateDirectory(targetSubDir);

            string file1Src = Path.Combine(srcDir, "file1.txt");
            string file1Dest = Path.Combine(targetSubDir, "file1.txt");
            await File.WriteAllTextAsync(file1Src, "Content 1");
            await File.WriteAllTextAsync(file1Dest, "Content 1");
            File.SetLastWriteTimeUtc(file1Dest, File.GetLastWriteTimeUtc(file1Src));

            string file2Src = Path.Combine(srcDir, "file2.txt");
            string file2Dest = Path.Combine(targetSubDir, "file2.txt");
            await File.WriteAllTextAsync(file2Src, "Content 2");

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpoints"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Resume Test Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Paused,
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
                        DestinationPath = file2Dest,
                        SourceLength = new FileInfo(file2Src).Length,
                        SourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(file2Src),
                        Status = CheckpointFileStatus.Pending,
                        BytesCopied = 0
                    }
                }
            };

            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job
            {
                Id = jobId,
                Name = "Resume Test Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                VerificationMode = VerificationMode.SizeAndTimestamp
            };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied); // file2 only
            Assert.Equal(1, result.FilesSkipped); // file1 validated and skipped
            Assert.True(File.Exists(file2Dest));
            Assert.Equal("Content 2", await File.ReadAllTextAsync(file2Dest));

            // Completed job deletes checkpoint
            Assert.Null(await checkpointRepo.GetCheckpointAsync(jobId));
        }

        [Fact]
        public async Task FileCopyService_SourceChangedSinceCheckpoint_ResetsStatusToPending()
        {
            string srcDir = Path.Combine(_testBaseDir, "SrcChanged");
            string destDir = Path.Combine(_testBaseDir, "DestChanged");
            string targetSubDir = Path.Combine(destDir, "SrcChanged");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            Directory.CreateDirectory(targetSubDir);

            string fileSrc = Path.Combine(srcDir, "doc.txt");
            string fileDest = Path.Combine(targetSubDir, "doc.txt");
            await File.WriteAllTextAsync(fileSrc, "UPDATED content!");
            await File.WriteAllTextAsync(fileDest, "OLD content");

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpoints"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Source Changed Test",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                TotalFiles = 1,
                CompletedFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "doc.txt",
                        SourcePath = fileSrc,
                        DestinationPath = fileDest,
                        SourceLength = 10, // Old length
                        SourceLastWriteTimeUtc = DateTime.UtcNow.AddHours(-1),
                        Status = CheckpointFileStatus.Completed,
                        BytesCopied = 10
                    }
                }
            };

            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job
            {
                Id = jobId,
                Name = "Source Changed Test",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied); // Overwritten because source changed
            Assert.Equal("UPDATED content!", await File.ReadAllTextAsync(fileDest));
        }

        [Fact]
        public async Task FileCopyService_PauseTokenTriggered_SavesCheckpointWithPausedState()
        {
            string srcDir = Path.Combine(_testBaseDir, "SrcPause");
            string destDir = Path.Combine(_testBaseDir, "DestPause");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            // Create multiple files
            for (int i = 1; i <= 5; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(srcDir, $"file_{i}.txt"), $"Content {i}");
            }

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpoints"));
            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);

            var pts = new PauseTokenSource();
            var job = new Job
            {
                Id = jobId,
                Name = "Pause Token Test",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            // Pause immediately
            pts.Pause();
            using var cts = new CancellationTokenSource(300);
            try
            {
                await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: cts.Token, pauseToken: pts.Token, resumeCheckpoint: null);
            }
            catch (OperationCanceledException) { }

            var cp = await checkpointRepo.GetCheckpointAsync(jobId);
            Assert.NotNull(cp);
            Assert.Equal(ExecutionState.Paused, cp!.CurrentState);
        }

        [Fact]
        public async Task FileCopyService_InterruptedCopyingFile_ResetsToPendingOnResume()
        {
            string srcDir = Path.Combine(_testBaseDir, "SrcLarge");
            string destDir = Path.Combine(_testBaseDir, "DestLarge");
            string targetSubDir = Path.Combine(destDir, "SrcLarge");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            Directory.CreateDirectory(targetSubDir);

            string fileSrc = Path.Combine(srcDir, "large.bin");
            string fileDest = Path.Combine(targetSubDir, "large.bin");
            byte[] dummyData = new byte[1024 * 100]; // 100KB
            await File.WriteAllBytesAsync(fileSrc, dummyData);

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpoints"));

            // Checkpoint where large.bin was left in Copying state
            var checkpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Interrupted Large File Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Failed,
                TotalFiles = 1,
                PendingFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "large.bin",
                        SourcePath = fileSrc,
                        DestinationPath = fileDest,
                        SourceLength = dummyData.Length,
                        SourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(fileSrc),
                        Status = CheckpointFileStatus.Copying, // Was copying when crash/kill occurred
                        BytesCopied = 50000
                    }
                }
            };

            await checkpointRepo.SaveCheckpointAsync(checkpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job
            {
                Id = jobId,
                Name = "Interrupted Large File Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, pauseToken: null, resumeCheckpoint: checkpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal(dummyData.Length, new FileInfo(fileDest).Length);
        }

        [Fact]
        public async Task FileCopyService_PreservesDestinationTimestamps_AllowingResumeValidationToPass()
        {
            string srcDir = Path.Combine(_testBaseDir, "SrcTs");
            string destDir = Path.Combine(_testBaseDir, "DestTs");
            string targetSubDir = Path.Combine(destDir, "SrcTs");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1Src = Path.Combine(srcDir, "file1.txt");
            await File.WriteAllTextAsync(file1Src, "File 1 Data");
            DateTime customTime = new DateTime(2025, 5, 10, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file1Src, customTime);

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpointsTs"));
            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);

            var job = new Job
            {
                Id = jobId,
                Name = "Timestamp Preservation Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                PreserveTimestamps = true,
                VerificationMode = VerificationMode.SizeAndTimestamp
            };

            // Run initial copy
            var initialResult = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None);
            Assert.True(initialResult.Success);
            Assert.Equal(1, initialResult.FilesCopied);

            string file1Dest = Path.Combine(targetSubDir, "file1.txt");
            Assert.True(File.Exists(file1Dest));

            // Verify destination LastWriteTimeUtc matches source within 2 seconds
            TimeSpan diff = File.GetLastWriteTimeUtc(file1Dest) > customTime
                ? File.GetLastWriteTimeUtc(file1Dest) - customTime
                : customTime - File.GetLastWriteTimeUtc(file1Dest);
            Assert.True(diff.TotalSeconds <= 2, $"Destination timestamp diff was {diff.TotalSeconds}s");

            // Simulate partial resume checkpoint with file1 marked Completed
            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Timestamp Preservation Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Paused,
                VerificationMode = VerificationMode.SizeAndTimestamp,
                TotalFiles = 1,
                CompletedFiles = 1,
                FileEntries = new List<CheckpointFileEntry>
                {
                    new CheckpointFileEntry
                    {
                        RelativePath = "file1.txt",
                        SourcePath = file1Src,
                        DestinationPath = file1Dest,
                        SourceLength = new FileInfo(file1Src).Length,
                        SourceLastWriteTimeUtc = customTime,
                        Status = CheckpointFileStatus.Completed,
                        BytesCopied = new FileInfo(file1Src).Length
                    }
                }
            };

            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            // Resume copy - file1 should be validated and SKIPPED, not recopied!
            var resumeResult = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);
            Assert.True(resumeResult.Success);
            Assert.Equal(0, resumeResult.FilesCopied);
            Assert.Equal(1, resumeResult.FilesSkipped);
        }

        [Fact]
        public async Task FileCopyService_MissingDestinationFile_TriggersRecopy()
        {
            string srcDir = Path.Combine(_testBaseDir, "SrcMissing");
            string destDir = Path.Combine(_testBaseDir, "DestMissing");
            string targetSubDir = Path.Combine(destDir, "SrcMissing");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1Src = Path.Combine(srcDir, "file1.txt");
            string file1Dest = Path.Combine(targetSubDir, "file1.txt");
            await File.WriteAllTextAsync(file1Src, "File 1 Content");
            // Do NOT create destination file (simulates deleted destination file)

            var jobId = Guid.NewGuid();
            var checkpointRepo = new CheckpointRepository(customDirectory: Path.Combine(_testBaseDir, "checkpointsMissing"));

            var resumeCheckpoint = new JobCheckpoint
            {
                JobId = jobId,
                JobName = "Missing Dest Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CurrentState = ExecutionState.Paused,
                VerificationMode = VerificationMode.SizeAndTimestamp,
                TotalFiles = 1,
                CompletedFiles = 1,
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
                    }
                }
            };

            await checkpointRepo.SaveCheckpointAsync(resumeCheckpoint);

            var service = new FileCopyService(logService: null, checkpointRepository: checkpointRepo);
            var job = new Job
            {
                Id = jobId,
                Name = "Missing Dest Job",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job: job, dryRun: false, progress: null, cancellationToken: CancellationToken.None, resumeCheckpoint: resumeCheckpoint, triggerSource: ExecutionTriggerSource.RecoveryResume);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied); // Recopied because destination file was missing
            Assert.Equal(0, result.FilesSkipped);
            Assert.True(File.Exists(file1Dest));
        }
    }
}
