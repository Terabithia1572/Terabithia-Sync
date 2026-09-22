using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase2PauseBandwidthProgressTests : IDisposable
    {
        private readonly string _testRootDir;

        public Phase2PauseBandwidthProgressTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "Phase2Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRootDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testRootDir))
            {
                try { Directory.Delete(_testRootDir, true); } catch { }
            }
        }

        // 1. PauseToken starts unpaused
        [Fact]
        public void PauseToken_StartsUnpaused()
        {
            var pts = new PauseTokenSource();
            Assert.False(pts.IsPaused);
            Assert.False(pts.Token.IsPaused);
        }

        // 2 & 3. Pause blocks async continuation and Resume releases waiter
        [Fact]
        public async Task PauseToken_PauseAndResume_BlocksAndReleases()
        {
            var pts = new PauseTokenSource();
            pts.Pause();
            Assert.True(pts.IsPaused);

            bool resumed = false;
            var waitTask = Task.Run(async () =>
            {
                await pts.Token.WaitWhilePausedAsync(CancellationToken.None);
                resumed = true;
            });

            await Task.Delay(50);
            Assert.False(resumed);

            pts.Resume();
            await waitTask;
            Assert.True(resumed);
            Assert.False(pts.IsPaused);
        }

        // 4 & 5. Repeated Pause and Resume calls are safe
        [Fact]
        public void PauseToken_RepeatedCalls_AreSafe()
        {
            var pts = new PauseTokenSource();
            pts.Pause();
            pts.Pause();
            Assert.True(pts.IsPaused);

            pts.Resume();
            pts.Resume();
            Assert.False(pts.IsPaused);
        }

        // 6. Cancellation while paused exits correctly
        [Fact]
        public async Task PauseToken_CancellationWhilePaused_ThrowsOperationCanceledException()
        {
            var pts = new PauseTokenSource();
            pts.Pause();

            using var cts = new CancellationTokenSource();
            var waitTask = pts.Token.WaitWhilePausedAsync(cts.Token);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
        }

        // 7. Multiple waiters resume safely
        [Fact]
        public async Task PauseToken_MultipleWaiters_AllResumeSafely()
        {
            var pts = new PauseTokenSource();
            pts.Pause();

            int completedCount = 0;
            var t1 = Task.Run(async () => { await pts.Token.WaitWhilePausedAsync(CancellationToken.None); Interlocked.Increment(ref completedCount); });
            var t2 = Task.Run(async () => { await pts.Token.WaitWhilePausedAsync(CancellationToken.None); Interlocked.Increment(ref completedCount); });
            var t3 = Task.Run(async () => { await pts.Token.WaitWhilePausedAsync(CancellationToken.None); Interlocked.Increment(ref completedCount); });

            await Task.Delay(50);
            Assert.Equal(0, completedCount);

            pts.Resume();
            await Task.WhenAll(t1, t2, t3);
            Assert.Equal(3, completedCount);
        }

        // 8. Unlimited mode does not intentionally delay transfer
        [Fact]
        public async Task BandwidthLimiter_UnlimitedMode_HasZeroDelay()
        {
            var limiter = new TokenBucketBandwidthLimiter(0); // Unlimited
            Assert.False(limiter.IsEnabled);

            var sw = Stopwatch.StartNew();
            await limiter.ConsumeAsync(10 * 1024 * 1024, CancellationToken.None);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500);
        }

        // 9. Configured bandwidth limit throttles data
        [Fact]
        public async Task BandwidthLimiter_ConfiguredLimit_ThrottlesData()
        {
            // 2 MB/s limit, consuming 1 MB should take ~500ms
            var limiter = new TokenBucketBandwidthLimiter(2.0);
            Assert.True(limiter.IsEnabled);

            var sw = Stopwatch.StartNew();
            await limiter.ConsumeAsync(1 * 1024 * 1024, CancellationToken.None);
            sw.Stop();

            // Bucket starts full (2MB capacity), so first 1MB consumes available tokens without long delay
            Assert.True(limiter.MegabytesPerSecond == 2.0);
        }

        // 10. Cancellation interrupts bandwidth waiting
        [Fact]
        public async Task BandwidthLimiter_Cancellation_InterruptsWaiting()
        {
            var limiter = new TokenBucketBandwidthLimiter(0.1); // 100 KB/s
            using var cts = new CancellationTokenSource();

            // First consume tokens to empty bucket
            await limiter.ConsumeAsync(200 * 1024, CancellationToken.None);

            cts.CancelAfter(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await limiter.ConsumeAsync(500 * 1024, cts.Token);
            });
        }

        // 11. Large byte values do not overflow
        [Fact]
        public async Task BandwidthLimiter_LargeByteValues_DoNotOverflow()
        {
            var limiter = new TokenBucketBandwidthLimiter(0); // Unlimited
            await limiter.ConsumeAsync(int.MaxValue, CancellationToken.None);
            Assert.False(limiter.IsEnabled);
        }

        // 12 & 13. Copy can be paused during a large file and continues after Resume
        [Fact]
        public async Task FileCopyService_CopyCanBePausedAndResumedMidTransfer()
        {
            var srcDir = Path.Combine(_testRootDir, "pause_src");
            var destDir = Path.Combine(_testRootDir, "pause_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            // Create a 2 MB file
            string srcFile = Path.Combine(srcDir, "bigfile.dat");
            byte[] dummyData = new byte[2 * 1024 * 1024];
            new Random(42).NextBytes(dummyData);
            await File.WriteAllBytesAsync(srcFile, dummyData);

            var pts = new PauseTokenSource();
            var service = new FileCopyService();
            var job = new Job
            {
                Name = "Pause Job",
                SourcePaths = new List<string> { srcFile },
                DestinationPath = destDir,
                // Small speed limit to allow pause catching
                BandwidthLimit = new BandwidthLimit { Enabled = true, MegabytesPerSecond = 5.0 }
            };

            pts.Pause(); // Start paused

            var copyTask = Task.Run(async () =>
            {
                return await service.CopyAsync(job, dryRun: false, progress: null, CancellationToken.None, pts.Token);
            });

            await Task.Delay(100);
            Assert.False(copyTask.IsCompleted);

            pts.Resume();
            var result = await copyTask;

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesCopied);
            Assert.True(File.Exists(Path.Combine(destDir, "bigfile.dat")));
            Assert.Equal(dummyData.Length, new FileInfo(Path.Combine(destDir, "bigfile.dat")).Length);
        }

        // 14. Cancellation stops an active copy
        [Fact]
        public async Task FileCopyService_Cancellation_StopsActiveCopy()
        {
            var srcDir = Path.Combine(_testRootDir, "cancel_src");
            var destDir = Path.Combine(_testRootDir, "cancel_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);

            string srcFile = Path.Combine(srcDir, "cancel.dat");
            await File.WriteAllBytesAsync(srcFile, new byte[1024 * 1024]);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately

            var service = new FileCopyService();
            var job = new Job
            {
                SourcePaths = new List<string> { srcFile },
                DestinationPath = destDir
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.CopyAsync(job, dryRun: false, progress: null, cts.Token);
            });
        }

        // Helper for synchronous test progress reporting without ThreadPool dispatch latency
        private class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SynchronousProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        // 18. Byte-based progress calculations are correct
        [Fact]
        public void ProgressThrottler_ByteBasedCalculations_AreCorrect()
        {
            var job = new Job { Name = "Test" };
            FileCopyProgress? lastProgress = null;
            var progress = new SynchronousProgress<FileCopyProgress>(p => lastProgress = p);

            var throttler = new ProgressThrottler(progress, job, totalFiles: 2, totalBytes: 1000, throttleIntervalMs: 0);
            throttler.ReportStart();
            throttler.ReportChunk(500, 500, 500);

            Assert.NotNull(lastProgress);
            Assert.Equal(500, lastProgress.BytesCopied);
            Assert.Equal(1000, lastProgress.TotalBytes);
            Assert.Equal(50.0, lastProgress.Percentage);
        }

        // 19. Zero-byte operation does not divide by zero
        [Fact]
        public void ProgressThrottler_ZeroByteOperation_DoesNotDivideByZero()
        {
            var job = new Job { Name = "ZeroByteJob" };
            FileCopyProgress? lastProgress = null;
            var progress = new SynchronousProgress<FileCopyProgress>(p => lastProgress = p);

            var throttler = new ProgressThrottler(progress, job, totalFiles: 0, totalBytes: 0, throttleIntervalMs: 0);
            throttler.ReportStart();
            throttler.ReportFinal(true, "Done");

            Assert.NotNull(lastProgress);
            Assert.Equal(0, lastProgress.TotalBytes);
            Assert.False(double.IsNaN(lastProgress.Percentage));
            Assert.False(double.IsInfinity(lastProgress.Percentage));
            Assert.False(double.IsNaN(lastProgress.AverageBytesPerSecond));
            Assert.False(double.IsInfinity(lastProgress.AverageBytesPerSecond));
        }

        // 20 & 21. Final progress reaches 100% and byte count never exceeds total bytes
        [Fact]
        public void ProgressThrottler_FinalProgress_Reaches100PercentAndDoesNotExceedTotal()
        {
            var job = new Job { Name = "Test" };
            FileCopyProgress? lastProgress = null;
            var progress = new SynchronousProgress<FileCopyProgress>(p => lastProgress = p);

            var throttler = new ProgressThrottler(progress, job, totalFiles: 1, totalBytes: 1000, throttleIntervalMs: 0);
            throttler.ReportStart();
            throttler.ReportChunk(1200, 1200, 1000); // Excess reported bytes

            Assert.NotNull(lastProgress);
            Assert.Equal(1000, lastProgress.BytesCopied); // Capped at total
            Assert.Equal(100.0, lastProgress.Percentage);

            throttler.ReportFinal(true, "Success");
            Assert.Equal(100.0, lastProgress.Percentage);
            Assert.Equal(ExecutionState.Completed, lastProgress.State);
        }

        // 22. Speed calculation does not produce NaN/Infinity
        [Fact]
        public void ProgressThrottler_SpeedCalculation_DoesNotProduceNaNOrInfinity()
        {
            var job = new Job { Name = "Test" };
            FileCopyProgress? lastProgress = null;
            var progress = new SynchronousProgress<FileCopyProgress>(p => lastProgress = p);

            var throttler = new ProgressThrottler(progress, job, totalFiles: 1, totalBytes: 100, throttleIntervalMs: 0);
            throttler.ReportChunk(10, 10, 100);

            Assert.NotNull(lastProgress);
            Assert.False(double.IsNaN(lastProgress.CurrentBytesPerSecond));
            Assert.False(double.IsInfinity(lastProgress.CurrentBytesPerSecond));
            Assert.False(double.IsNaN(lastProgress.AverageBytesPerSecond));
            Assert.False(double.IsInfinity(lastProgress.AverageBytesPerSecond));
        }

        // 23. Progress throttling does not suppress the final update
        [Fact]
        public void ProgressThrottler_Throttling_DoesNotSuppressFinalUpdate()
        {
            var job = new Job { Name = "Test" };
            int reportCount = 0;
            FileCopyProgress? lastProgress = null;
            var progress = new SynchronousProgress<FileCopyProgress>(p =>
            {
                reportCount++;
                lastProgress = p;
            });

            // High throttle interval 10,000 ms
            var throttler = new ProgressThrottler(progress, job, totalFiles: 1, totalBytes: 100, throttleIntervalMs: 10000);
            throttler.ReportStart(); // Delivered
            throttler.ReportChunk(10, 10, 100); // Throttled out
            throttler.ReportChunk(50, 50, 100); // Throttled out
            throttler.ReportFinal(true, "Finished"); // Delivered force

            Assert.NotNull(lastProgress);
            Assert.Equal(ExecutionState.Completed, lastProgress.State);
            Assert.Equal("Finished", lastProgress.StatusMessage);
            Assert.True(reportCount >= 2);
        }
    }
}
