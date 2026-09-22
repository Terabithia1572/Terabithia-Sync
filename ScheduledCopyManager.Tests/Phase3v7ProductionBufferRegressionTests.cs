using System;
using System.Buffers;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v7ProductionBufferRegressionTests : IDisposable
    {
        private readonly string _testDir;

        public Phase3v7ProductionBufferRegressionTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "Phase3v7Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                    Directory.Delete(_testDir, true);
            }
            catch { }
        }

        [Fact]
        public void FileCopyService_Constants_ConfiguredForProductionPerformance()
        {
            var bufferSizeField = typeof(FileCopyService).GetField("BufferSize", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var streamBufferField = typeof(FileCopyService).GetField("FileStreamInternalBufferSize", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            Assert.NotNull(bufferSizeField);
            Assert.NotNull(streamBufferField);

            int bufferSize = (int)bufferSizeField.GetValue(null)!;
            int streamBuffer = (int)streamBufferField.GetValue(null)!;

            Assert.Equal(512 * 1024, bufferSize); // 512 KiB production app buffer
            Assert.Equal(4096, streamBuffer);          // Conservative 4 KB stream buffer
        }

        [Fact]
        public async Task CopyAsync_SuccessPath_ReturnsArrayPoolBuffer()
        {
            var srcDir = Path.Combine(_testDir, "src_success");
            var destDir = Path.Combine(_testDir, "dest_success");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string srcFile = Path.Combine(srcDir, "file_10MB.dat");
            byte[] dummyData = new byte[10 * 1024 * 1024]; // 10 MB
            new Random(1).NextBytes(dummyData);
            await File.WriteAllBytesAsync(srcFile, dummyData);

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "v7SuccessJob",
                SourcePaths = new System.Collections.Generic.List<string> { srcDir },
                DestinationPath = destDir
            };

            var service = new FileCopyService();
            var result = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.Equal(dummyData.Length, result.BytesCopied);
        }

        [Fact]
        public async Task CopyAsync_Cancellation_ReturnsArrayPoolBufferAndThrows()
        {
            var srcDir = Path.Combine(_testDir, "src_cancel");
            var destDir = Path.Combine(_testDir, "dest_cancel");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string srcFile = Path.Combine(srcDir, "file_20MB.dat");
            byte[] dummyData = new byte[20 * 1024 * 1024];
            await File.WriteAllBytesAsync(srcFile, dummyData);

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "v7CancelJob",
                SourcePaths = new System.Collections.Generic.List<string> { srcDir },
                DestinationPath = destDir
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately

            var service = new FileCopyService();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, false, null, cts.Token);
            });
        }

        [Fact]
        public async Task CopyAsync_PauseToken_SuspendsAndResumesCleanly()
        {
            var srcDir = Path.Combine(_testDir, "src_pause");
            var destDir = Path.Combine(_testDir, "dest_pause");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string srcFile = Path.Combine(srcDir, "file_5MB.dat");
            byte[] dummyData = new byte[5 * 1024 * 1024];
            await File.WriteAllBytesAsync(srcFile, dummyData);

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "v7PauseJob",
                SourcePaths = new System.Collections.Generic.List<string> { srcDir },
                DestinationPath = destDir
            };

            var pauseTokenSource = new PauseTokenSource();
            var service = new FileCopyService();

            var copyTask = service.CopyAsync(job, false, null, CancellationToken.None, pauseTokenSource.Token);

            pauseTokenSource.Pause();
            Assert.True(pauseTokenSource.Token.IsPaused);

            pauseTokenSource.Resume();
            Assert.False(pauseTokenSource.Token.IsPaused);

            var result = await copyTask;
            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
        }
    }
}
