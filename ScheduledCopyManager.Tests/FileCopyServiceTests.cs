using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class FileCopyServiceTests : IDisposable
    {
        private readonly string _testRootDir;

        public FileCopyServiceTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "TerabithiaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRootDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testRootDir))
            {
                try { Directory.Delete(_testRootDir, true); } catch { }
            }
        }

        // 1. Single File Copy
        [Fact]
        public async Task CopyAsync_1_SingleFileCopy_PreservesFileNameAndCopiesContent()
        {
            var srcDir = Path.Combine(_testRootDir, "single_file_src");
            var destDir = Path.Combine(_testRootDir, "single_file_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file = Path.Combine(srcDir, "test.zip");
            File.WriteAllText(file, "Zip File Content");

            var service = new FileCopyService();
            var job = new Job
            {
                Name = "Single File Job",
                SourcePaths = new List<string> { file },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.True(File.Exists(Path.Combine(destDir, "test.zip")));
            Assert.Equal("Zip File Content", File.ReadAllText(Path.Combine(destDir, "test.zip")));
        }

        // 2. Single Folder Copy
        [Fact]
        public async Task CopyAsync_2_SingleFolderCopy_PreservesFolderStructure()
        {
            var srcDir = Path.Combine(_testRootDir, "single_folder");
            var destDir = Path.Combine(_testRootDir, "dest_single");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "data.txt"), "data");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(destDir, "single_folder", "data.txt")));
        }

        // 3. Folder Root Preservation (Crucial Requirement Test)
        // Source: C:\Test\Release, Destination: E:\Backup => Destination result: E:\Backup\Release\file.txt (NOT E:\Backup\file.txt)
        [Fact]
        public async Task CopyAsync_3_FolderRootPreservation_PreservesSelectedSourceDirectoryName()
        {
            var releaseFolder = Path.Combine(_testRootDir, "Release");
            var backupFolder = Path.Combine(_testRootDir, "Backup");
            Directory.CreateDirectory(releaseFolder);
            Directory.CreateDirectory(Path.Combine(releaseFolder, "subfolder"));

            File.WriteAllText(Path.Combine(releaseFolder, "file1.exe"), "Binary 1");
            File.WriteAllText(Path.Combine(releaseFolder, "file2.dll"), "Library 2");
            File.WriteAllText(Path.Combine(releaseFolder, "subfolder", "file3.dll"), "Library 3");

            var service = new FileCopyService();
            var job = new Job
            {
                Name = "Release Backup",
                SourcePaths = new List<string> { releaseFolder },
                DestinationPath = backupFolder
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(3, result.FilesCopied);

            // Assert root folder preservation: Backup/Release/...
            Assert.True(Directory.Exists(Path.Combine(backupFolder, "Release")));
            Assert.True(File.Exists(Path.Combine(backupFolder, "Release", "file1.exe")));
            Assert.True(File.Exists(Path.Combine(backupFolder, "Release", "file2.dll")));
            Assert.True(File.Exists(Path.Combine(backupFolder, "Release", "subfolder", "file3.dll")));
            
            // Assert that files were NOT dumped into Backup root
            Assert.False(File.Exists(Path.Combine(backupFolder, "file1.exe")));
        }

        // 4. Multiple Folder Copy
        [Fact]
        public async Task CopyAsync_4_MultipleFolderCopy_PreservesAllSelectedSourceRoots()
        {
            var rootDir = Path.Combine(_testRootDir, "Sources");
            var releaseDir = Path.Combine(rootDir, "Release");
            var docsDir = Path.Combine(rootDir, "Documents");
            var dbDir = Path.Combine(rootDir, "Database");
            var destDir = Path.Combine(_testRootDir, "BackupTarget");

            Directory.CreateDirectory(releaseDir);
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(dbDir);

            File.WriteAllText(Path.Combine(releaseDir, "app.exe"), "App");
            File.WriteAllText(Path.Combine(docsDir, "readme.txt"), "Readme");
            File.WriteAllText(Path.Combine(dbDir, "data.db"), "DB");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { releaseDir, docsDir, dbDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(3, result.FilesCopied);
            Assert.True(File.Exists(Path.Combine(destDir, "Release", "app.exe")));
            Assert.True(File.Exists(Path.Combine(destDir, "Documents", "readme.txt")));
            Assert.True(File.Exists(Path.Combine(destDir, "Database", "data.db")));
        }

        // 5. Incremental Copy
        [Fact]
        public async Task CopyAsync_5_IncrementalCopy_SkipsUnmodifiedFiles()
        {
            var srcDir = Path.Combine(_testRootDir, "inc_src");
            var destDir = Path.Combine(_testRootDir, "inc_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string file1 = Path.Combine(srcDir, "unmodified.txt");
            string file2 = Path.Combine(srcDir, "modified.txt");
            File.WriteAllText(file1, "Same");
            File.WriteAllText(file2, "New Version");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Incremental
            };

            // First run
            await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            // Second run without modifications
            var result2 = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);
            Assert.True(result2.Success);
            Assert.Equal(0, result2.FilesCopied);
            Assert.Equal(2, result2.FilesSkipped);
        }

        // 6. Mirror Copy
        [Fact]
        public async Task CopyAsync_6_MirrorCopy_RemovesStaleFilesOnlyInsideTargetSubdir()
        {
            var srcDir = Path.Combine(_testRootDir, "mirror_src");
            var destDir = Path.Combine(_testRootDir, "mirror_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            File.WriteAllText(Path.Combine(srcDir, "valid.txt"), "valid");

            // Destination has a file inside target subfolder AND an unrelated file outside it
            string targetFolder = Path.Combine(destDir, "mirror_src");
            Directory.CreateDirectory(targetFolder);
            File.WriteAllText(Path.Combine(targetFolder, "stale.txt"), "stale");

            string unrelatedFolder = Path.Combine(destDir, "OtherFolder");
            Directory.CreateDirectory(unrelatedFolder);
            string unrelatedFile = Path.Combine(unrelatedFolder, "important.txt");
            File.WriteAllText(unrelatedFile, "do not delete");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(targetFolder, "valid.txt")));
            Assert.False(File.Exists(Path.Combine(targetFolder, "stale.txt"))); // Stale file inside target removed
            Assert.True(File.Exists(unrelatedFile)); // Unrelated outside folder untouched!
        }

        // 7. Verify-Only Mode
        [Fact]
        public async Task CopyAsync_7_VerifyOnlyMode_DoesNotCreateOrModifyFiles()
        {
            var srcDir = Path.Combine(_testRootDir, "verify_src");
            var destDir = Path.Combine(_testRootDir, "verify_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            File.WriteAllText(Path.Combine(srcDir, "missing.txt"), "missing");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.VerifyOnly
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(JobResultStatus.Failure, result.Status);
            Assert.False(File.Exists(Path.Combine(destDir, "verify_src", "missing.txt")));
        }

        // 8. Skip Conflict Policy
        [Fact]
        public async Task CopyAsync_8_SkipConflict_SkipsExistingFiles()
        {
            var srcDir = Path.Combine(_testRootDir, "skip_src");
            var destDir = Path.Combine(_testRootDir, "skip_dest");
            Directory.CreateDirectory(srcDir);
            
            string targetFolder = Path.Combine(destDir, "skip_src");
            Directory.CreateDirectory(targetFolder);

            File.WriteAllText(Path.Combine(srcDir, "file.txt"), "Source Data");
            File.WriteAllText(Path.Combine(targetFolder, "file.txt"), "Destination Original Data");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                ConflictPolicy = ConflictPolicy.Skip
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesSkipped);
            Assert.Equal("Destination Original Data", File.ReadAllText(Path.Combine(targetFolder, "file.txt")));
        }

        // 9. Overwrite Conflict Policy
        [Fact]
        public async Task CopyAsync_9_OverwriteConflict_ReplacesExistingFile()
        {
            var srcDir = Path.Combine(_testRootDir, "ow_src");
            var destDir = Path.Combine(_testRootDir, "ow_dest");
            Directory.CreateDirectory(srcDir);

            string targetFolder = Path.Combine(destDir, "ow_src");
            Directory.CreateDirectory(targetFolder);

            string srcFile = Path.Combine(srcDir, "file.txt");
            string destFile = Path.Combine(targetFolder, "file.txt");
            File.WriteAllText(destFile, "Old Content");
            File.WriteAllText(srcFile, "Brand New Substantially Longer Updated Content");
            File.SetLastWriteTimeUtc(srcFile, DateTime.UtcNow.AddMinutes(5));

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                ConflictPolicy = ConflictPolicy.Overwrite
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal("Brand New Substantially Longer Updated Content", File.ReadAllText(Path.Combine(targetFolder, "file.txt")));
        }

        // 10. Rename Conflict Policy
        [Fact]
        public async Task CopyAsync_10_RenameConflict_CreatesNumberSuffixes()
        {
            var srcDir = Path.Combine(_testRootDir, "ren_src");
            var destDir = Path.Combine(_testRootDir, "ren_dest");
            Directory.CreateDirectory(srcDir);

            string targetFolder = Path.Combine(destDir, "ren_src");
            Directory.CreateDirectory(targetFolder);

            File.WriteAllText(Path.Combine(srcDir, "file.txt"), "New Data");
            File.WriteAllText(Path.Combine(targetFolder, "file.txt"), "Original Data");
            File.WriteAllText(Path.Combine(targetFolder, "file (1).txt"), "First Suffix");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                ConflictPolicy = ConflictPolicy.Rename
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(targetFolder, "file.txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "file (1).txt")));
            Assert.True(File.Exists(Path.Combine(targetFolder, "file (2).txt")));
            Assert.Equal("New Data", File.ReadAllText(Path.Combine(targetFolder, "file (2).txt")));
        }

        // 15. Cancellation
        [Fact]
        public async Task CopyAsync_15_Cancellation_AbortsProcess()
        {
            var srcDir = Path.Combine(_testRootDir, "cancel_src");
            var destDir = Path.Combine(_testRootDir, "cancel_dest");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "file.txt"), "Data");

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, dryRun: false, progress: null, cts.Token);
            });
        }

        // 16. Empty Directory Handling
        [Fact]
        public async Task CopyAsync_16_EmptyDirectory_HandledWithoutErrors()
        {
            var srcDir = Path.Combine(_testRootDir, "empty_src");
            var destDir = Path.Combine(_testRootDir, "empty_dest");
            Directory.CreateDirectory(srcDir);

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(0, result.FilesCopied);
        }

        // 17. Large File Handling
        [Fact]
        public async Task CopyAsync_17_LargeFileHandling_StreamsSuccessfully()
        {
            var srcDir = Path.Combine(_testRootDir, "large_src");
            var destDir = Path.Combine(_testRootDir, "large_dest");
            Directory.CreateDirectory(srcDir);

            string largeFilePath = Path.Combine(srcDir, "large.bin");
            byte[] dummyData = new byte[1024 * 1024 * 2]; // 2 MB
            new Random(42).NextBytes(dummyData);
            await File.WriteAllBytesAsync(largeFilePath, dummyData);

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { largeFilePath },
                DestinationPath = destDir
            };

            var result = await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal(dummyData.Length, new FileInfo(Path.Combine(destDir, "large.bin")).Length);
        }
    }
}
