using System;
using System.Buffers;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v8VmBufferDiagnosticTests
    {
        [Theory]
        [InlineData(524288, 524288)]   // 512 KiB
        [InlineData(1048576, 1048576)] // 1 MiB
        [InlineData(2097152, 2097152)] // 2 MiB
        [InlineData(4194304, 4194304)] // 4 MiB
        public void Settings_GetValidatedCopyBufferSize_ValidValuesMapCorrectly(int inputBytes, int expectedBytes)
        {
            var settings = new Settings { CopyBufferSize = inputBytes };
            Assert.Equal(expectedBytes, settings.GetValidatedCopyBufferSize());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(8192)]
        [InlineData(65536)]
        [InlineData(16777216)]
        [InlineData(9999999)]
        public void Settings_GetValidatedCopyBufferSize_InvalidValuesFallbackTo4MiB(int invalidInput)
        {
            var settings = new Settings { CopyBufferSize = invalidInput };
            Assert.Equal(4194304, settings.GetValidatedCopyBufferSize());
        }

        [Fact]
        public void FileCopyService_FileStreamInternalBuffer_RemainsFixedAt4096()
        {
            var field = typeof(FileCopyService).GetField("FileStreamInternalBufferSize", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            Assert.NotNull(field);
            int value = (int)field.GetValue(null)!;
            Assert.Equal(4096, value);
        }

        [Fact]
        public async Task FileCopyService_UsesConfiguredCopyBufferSizeFromSettings()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "v8_buffer_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string sourceFile = Path.Combine(tempDir, "source.dat");
            string destDir = Path.Combine(tempDir, "dest");
            Directory.CreateDirectory(destDir);

            try
            {
                byte[] data = new byte[2 * 1024 * 1024]; // 2 MiB test file
                new Random(42).NextBytes(data);
                await File.WriteAllBytesAsync(sourceFile, data);

                var settingsRepo = new MemorySettingsRepository(new Settings { CopyBufferSize = 524288 }); // 512 KiB
                var service = new FileCopyService(settingsRepository: settingsRepo);

                var job = new Job
                {
                    Id = Guid.NewGuid(),
                    Name = "V8 Buffer Settings Test",
                    SourcePaths = new System.Collections.Generic.List<string> { sourceFile },
                    DestinationPath = destDir
                };

                var result = await service.CopyAsync(job, false, null, CancellationToken.None);
                Assert.True(result.Success);

                string destFile = Path.Combine(destDir, "source.dat");
                Assert.True(File.Exists(destFile));
                Assert.Equal(data.Length, new FileInfo(destFile).Length);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task FileCopyService_ArrayPoolBufferReturned_UnderSuccessCancelAndException()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "v8_pool_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string sourceFile = Path.Combine(tempDir, "source.dat");
            string destDir = Path.Combine(tempDir, "dest");
            Directory.CreateDirectory(destDir);

            try
            {
                byte[] data = new byte[1 * 1024 * 1024];
                await File.WriteAllBytesAsync(sourceFile, data);

                var service = new FileCopyService();

                // 1. Success case
                var jobSuccess = new Job
                {
                    Id = Guid.NewGuid(),
                    Name = "V8 Pool Success Test",
                    SourcePaths = new System.Collections.Generic.List<string> { sourceFile },
                    DestinationPath = destDir
                };
                var resSuccess = await service.CopyAsync(jobSuccess, false, null, CancellationToken.None);
                Assert.True(resSuccess.Success);

                // 2. Cancellation case
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var jobCancel = new Job
                {
                    Id = Guid.NewGuid(),
                    Name = "V8 Pool Cancel Test",
                    SourcePaths = new System.Collections.Generic.List<string> { sourceFile },
                    DestinationPath = Path.Combine(tempDir, "dest_cancel")
                };
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await service.CopyAsync(jobCancel, false, null, cts.Token);
                });

                // 3. Exception case (non-existent source)
                var jobException = new Job
                {
                    Id = Guid.NewGuid(),
                    Name = "V8 Pool Exception Test",
                    SourcePaths = new System.Collections.Generic.List<string> { Path.Combine(tempDir, "non_existent.dat") },
                    DestinationPath = Path.Combine(tempDir, "dest_ex")
                };
                var resEx = await service.CopyAsync(jobException, false, null, CancellationToken.None);
                Assert.False(resEx.Success);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        private class MemorySettingsRepository : ISettingsRepository
        {
            private Settings _settings;

            public MemorySettingsRepository(Settings settings)
            {
                _settings = settings;
            }

            public Task<Settings> GetAsync() => Task.FromResult(_settings);

            public Task SaveAsync(Settings settings)
            {
                _settings = settings;
                return Task.CompletedTask;
            }
        }
    }
}
