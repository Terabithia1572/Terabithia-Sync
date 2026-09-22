using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Helpers;
using ScheduledCopyManager.Presentation.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase2_5LiveTransferUiTests
    {
        [Fact]
        public void FormattingHelpers_FormatBytes_ReturnsCorrectHumanReadableUnits()
        {
            Assert.Equal("0 B", FormattingHelpers.FormatBytes(0));
            Assert.Equal("500 B", FormattingHelpers.FormatBytes(500));
            Assert.Equal("1,0 KB", FormattingHelpers.FormatBytes(1024));
            Assert.Equal("28,4 MB", FormattingHelpers.FormatBytes((long)(28.4 * 1024 * 1024)));
            Assert.Equal("1,2 GB", FormattingHelpers.FormatBytes((long)(1.2 * 1024 * 1024 * 1024)));
        }

        [Fact]
        public void FormattingHelpers_FormatSpeed_ReturnsCorrectHumanReadableSpeed()
        {
            Assert.Equal("0 B/s", FormattingHelpers.FormatSpeed(0));
            Assert.Equal("0 B/s", FormattingHelpers.FormatSpeed(double.NaN));
            Assert.Equal("0 B/s", FormattingHelpers.FormatSpeed(double.PositiveInfinity));
            Assert.Equal("0 B/s", FormattingHelpers.FormatSpeed(-100));
            Assert.Equal("500 B/s", FormattingHelpers.FormatSpeed(500));
            Assert.Equal("84,6 MB/s", FormattingHelpers.FormatSpeed(84.6 * 1024 * 1024));
        }

        [Fact]
        public void FormattingHelpers_FormatRemainingTime_HandlesInvalidAndPausedValuesSafely()
        {
            Assert.Equal("Duraklatıldı", FormattingHelpers.FormatRemainingTime(TimeSpan.FromSeconds(120), ExecutionState.Paused));
            Assert.Equal("Hesaplanıyor...", FormattingHelpers.FormatRemainingTime(TimeSpan.Zero, ExecutionState.Running));
            Assert.Equal("Hesaplanıyor...", FormattingHelpers.FormatRemainingTime(TimeSpan.FromDays(15), ExecutionState.Running));
            Assert.Equal("~02:14", FormattingHelpers.FormatRemainingTime(TimeSpan.FromSeconds(134), ExecutionState.Running));
            Assert.Equal("~01:05:10", FormattingHelpers.FormatRemainingTime(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(5)).Add(TimeSpan.FromSeconds(10)), ExecutionState.Running));
        }

        [Fact]
        public void FormattingHelpers_TrimPath_TrimsLongPathsCorrectly()
        {
            string shortPath = @"C:\Folder\File.txt";
            Assert.Equal(shortPath, FormattingHelpers.TrimPath(shortPath));

            string longPath = @"C:\Users\Username\Documents\SubFolder1\SubFolder2\SubFolder3\Deep\VeryLongFileName.xlsx";
            string trimmed = FormattingHelpers.TrimPath(longPath, 35);
            Assert.True(trimmed.Length <= 35 || trimmed.StartsWith("..."));
            Assert.Contains("VeryLongFileName.xlsx", trimmed);
        }

        [Fact]
        public void JobExecutionManager_SessionTracking_AssociatesWithCorrectJobId()
        {
            var manager = new JobExecutionManager();
            var job = new Job { Name = "Test Job 1" };

            using var session = manager.RegisterSession(job);

            Assert.True(manager.IsJobRunning(job.Id));
            Assert.NotNull(session);
            Assert.Equal(job.Id, session.JobId);
            Assert.Equal(job.Name, session.JobName);
        }

        [Fact]
        public void JobExecutionManager_PauseAndResume_UpdatesStateCorrectly()
        {
            var manager = new JobExecutionManager();
            var job = new Job { Name = "Pause/Resume Job" };

            using var session = manager.RegisterSession(job);

            Assert.True(manager.PauseJob(job.Id));
            Assert.True(session.PauseTokenSource.IsPaused);
            Assert.Equal(ExecutionState.Paused, session.CurrentProgress.State);

            Assert.True(manager.ResumeJob(job.Id));
            Assert.False(session.PauseTokenSource.IsPaused);
            Assert.Equal(ExecutionState.Running, session.CurrentProgress.State);
        }

        [Fact]
        public void JobExecutionManager_Cancel_TriggersCancellationToken()
        {
            var manager = new JobExecutionManager();
            var job = new Job { Name = "Cancel Job" };

            using var session = manager.RegisterSession(job);

            Assert.True(manager.CancelJob(job.Id));
            Assert.True(session.CancellationTokenSource.IsCancellationRequested);
            Assert.Equal(ExecutionState.Cancelled, session.CurrentProgress.State);
            Assert.False(manager.IsJobRunning(job.Id));
        }

        [Fact]
        public void JobExecutionManager_MultipleRunningJobs_KeepsProgressIndependent()
        {
            var manager = new JobExecutionManager();
            var job1 = new Job { Name = "Job 1" };
            var job2 = new Job { Name = "Job 2" };

            using var session1 = manager.RegisterSession(job1);
            using var session2 = manager.RegisterSession(job2);

            session1.Progress.Report(new FileCopyProgress { JobId = job1.Id, JobName = "Job 1", BytesCopied = 1000, TotalBytes = 2000 });
            session2.Progress.Report(new FileCopyProgress { JobId = job2.Id, JobName = "Job 2", BytesCopied = 5000, TotalBytes = 10000 });

            var prog1 = manager.GetJobProgress(job1.Id);
            var prog2 = manager.GetJobProgress(job2.Id);

            Assert.NotNull(prog1);
            Assert.NotNull(prog2);
            Assert.Equal(1000, prog1.BytesCopied);
            Assert.Equal(5000, prog2.BytesCopied);
            Assert.Equal(50.0, prog1.Percentage);
            Assert.Equal(50.0, prog2.Percentage);
        }

        [Fact]
        public void Job_BandwidthRetryVerification_Persistence()
        {
            var job = new Job
            {
                Name = "Full Config Job",
                BandwidthLimit = new BandwidthLimit { Enabled = true, MegabytesPerSecond = 25 },
                RetryPolicy = new RetryPolicy
                {
                    Enabled = true,
                    MaxAttempts = 5,
                    DelaySeconds = 10,
                    UseExponentialBackoff = false,
                    RetryLockedFiles = true,
                    WaitForDestination = true,
                    ResumeWhenDestinationReturns = true,
                    FailureBehavior = FailureBehavior.PauseJob
                },
                VerificationMode = VerificationMode.SHA256,
                VerifyCopy = true
            };

            string json = System.Text.Json.JsonSerializer.Serialize(job);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<Job>(json);

            Assert.NotNull(deserialized);
            Assert.True(deserialized.BandwidthLimit?.Enabled);
            Assert.Equal(25, deserialized.BandwidthLimit?.MegabytesPerSecond);
            Assert.Equal(5, deserialized.RetryPolicy?.MaxAttempts);
            Assert.Equal(10, deserialized.RetryPolicy?.DelaySeconds);
            Assert.False(deserialized.RetryPolicy?.UseExponentialBackoff);
            Assert.Equal(FailureBehavior.PauseJob, deserialized.RetryPolicy?.FailureBehavior);
            Assert.Equal(VerificationMode.SHA256, deserialized.VerificationMode);
            Assert.True(deserialized.VerifyCopy);
        }

        [Fact]
        public void LocalizationService_Phase2_5_Keys_ExistInTurkishAndEnglish()
        {
            var localization = new LocalizationService();

            localization.SetLanguage("tr-TR");
            Assert.Equal("Aktif Kopyalama Görevleri", localization.GetString("ActiveJobsTitle"));
            Assert.Equal("DURAKLATILDI", localization.GetString("StatusPaused"));
            Assert.Equal("Aktarım Hızı Sınırı", localization.GetString("BandwidthLimitTitle"));
            Assert.Equal("SHA-256", localization.GetString("VerificationSha256"));

            localization.SetLanguage("en-US");
            Assert.Equal("Active Copy Jobs", localization.GetString("ActiveJobsTitle"));
            Assert.Equal("PAUSED", localization.GetString("StatusPaused"));
            Assert.Equal("Transfer Speed Limit", localization.GetString("BandwidthLimitTitle"));
            Assert.Equal("SHA-256", localization.GetString("VerificationSha256"));
        }

        [Theory]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(25)]
        public async Task TokenBucketBandwidthLimiter_EnforcesRate(double megabytesPerSecond)
        {
            var limiter = new TokenBucketBandwidthLimiter(megabytesPerSecond);
            Assert.True(limiter.IsEnabled);
            Assert.Equal(megabytesPerSecond, limiter.MegabytesPerSecond);

            int chunkSize = 81920; // 80 KB
            long totalBytesToTest = (long)(megabytesPerSecond * 1024 * 1024 * 0.25); // 0.25 seconds worth of data
            long bytesConsumed = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (bytesConsumed < totalBytesToTest)
            {
                await limiter.ConsumeAsync(chunkSize);
                bytesConsumed += chunkSize;
            }
            sw.Stop();

            // Expected duration is at least 0.20 seconds (allowing tolerance for scheduling)
            // But must NOT complete instantly in 1-10 ms!
            double elapsedSec = sw.Elapsed.TotalSeconds;
            double actualMBps = (bytesConsumed / (1024.0 * 1024.0)) / elapsedSec;

            Assert.True(elapsedSec >= 0.15, $"Limiter completed too fast in {elapsedSec:F3}s (Speed: {actualMBps:F2} MB/s)");
        }

        [Fact]
        public async Task TokenBucketBandwidthLimiter_NoStartupBurst()
        {
            // 10 MB/s limit -> 10,485,760 B/s
            var limiter = new TokenBucketBandwidthLimiter(10.0);
            int chunkSize = 81920; // 80 KB
            int numChunks = 20; // 1.6 MB total

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < numChunks; i++)
            {
                await limiter.ConsumeAsync(chunkSize);
            }
            sw.Stop();

            // 1.6 MB at 10 MB/s should take ~0.16 seconds (minimum 0.10s)
            Assert.True(sw.Elapsed.TotalSeconds >= 0.10, $"First 1.6 MB completed in {sw.Elapsed.TotalSeconds:F3}s, indicating startup burst");
        }

        [Fact]
        public async Task TokenBucketBandwidthLimiter_PauseResume_DoesNotAccumulateBurstCredit()
        {
            var limiter = new TokenBucketBandwidthLimiter(10.0);
            
            // Consume initial chunk
            await limiter.ConsumeAsync(81920);

            // Simulate 2 seconds pause delay
            await Task.Delay(200);

            // After resume, transferring 2 MB must still be rate limited, not burst instantly
            int chunkSize = 81920;
            int numChunks = 25; // ~2.0 MB

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < numChunks; i++)
            {
                await limiter.ConsumeAsync(chunkSize);
            }
            sw.Stop();

            Assert.True(sw.Elapsed.TotalSeconds >= 0.12, $"Post-pause copy completed in {sw.Elapsed.TotalSeconds:F3}s, indicating burst accumulation");
        }

        [Fact]
        public async Task TokenBucketBandwidthLimiter_UnlimitedAndDisabled_DoNotThrottle()
        {
            var unlimitedLimiter = new TokenBucketBandwidthLimiter(0);
            Assert.False(unlimitedLimiter.IsEnabled);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
            {
                await unlimitedLimiter.ConsumeAsync(81920);
            }
            sw.Stop();

            Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"Unlimited limiter introduced delay: {sw.Elapsed.TotalMilliseconds}ms");
        }

        [Fact]
        public async Task TokenBucketBandwidthLimiter_Cancellation_ThrowsOperationCanceledException()
        {
            var limiter = new TokenBucketBandwidthLimiter(1.0); // 1 MB/s slow rate
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await limiter.ConsumeAsync(524288, cts.Token);
            });
        }
    }
}
