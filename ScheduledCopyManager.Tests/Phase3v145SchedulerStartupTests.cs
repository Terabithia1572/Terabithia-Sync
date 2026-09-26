using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3v145SchedulerStartupTests : IDisposable
    {
        private readonly string _testRootDir;
        private readonly TestLogService _logService;
        private readonly CheckpointRepository _checkpointRepository;

        public Phase3v145SchedulerStartupTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "Terabithia_v145Tests_" + Guid.NewGuid().ToString("N"));
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

        private class TestJobRepository : IJobRepository
        {
            private readonly Dictionary<Guid, Job> _jobs = new();
            public Task AddAsync(Job job) { _jobs[job.Id] = job; return Task.CompletedTask; }
            public Task DeleteAsync(Guid id) { _jobs.Remove(id); return Task.CompletedTask; }
            public Task<IReadOnlyList<Job>> GetAllAsync() => Task.FromResult<IReadOnlyList<Job>>(_jobs.Values.ToList().AsReadOnly());
            public Task<Job?> GetByIdAsync(Guid id) => Task.FromResult(_jobs.TryGetValue(id, out var j) ? j : null);
            public Task UpdateAsync(Job job) { _jobs[job.Id] = job; return Task.CompletedTask; }
        }

        private class TestHistoryRepository : IHistoryRepository
        {
            private readonly List<HistoryEntry> _entries = new();
            public Task AddAsync(HistoryEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
            public Task ClearAllAsync() { _entries.Clear(); return Task.CompletedTask; }
            public Task DeleteAsync(HistoryEntry entry) { _entries.RemoveAll(e => e.Id == entry.Id); return Task.CompletedTask; }
            public Task UpdateAsync(HistoryEntry entry) { return Task.CompletedTask; }
            public Task<IReadOnlyList<HistoryEntry>> GetAllAsync() => Task.FromResult<IReadOnlyList<HistoryEntry>>(_entries.AsReadOnly());
            public Task<IReadOnlyList<HistoryEntry>> GetByJobIdAsync(Guid jobId) => Task.FromResult<IReadOnlyList<HistoryEntry>>(_entries.Where(e => e.JobId == jobId).ToList().AsReadOnly());
            public Task<HistoryEntry?> GetByIdAsync(Guid id) => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));
        }

        private class TestUsbDriveService : IUsbDriveService
        {
            public event EventHandler<UsbDriveInfo>? DriveArrived;
            public event EventHandler<string>? DriveRemoved;
            public bool Connected { get; set; } = true;
            public string Serial { get; set; } = "VOL-145";
            public IReadOnlyList<UsbDriveInfo> GetRemovableDrives() => new List<UsbDriveInfo>();
            public bool IsDriveConnected(string drivePath) => Connected;
            public bool IsDriveConnected(string drivePath, string? targetVolumeSerialNumber) => Connected && (targetVolumeSerialNumber == null || targetVolumeSerialNumber == Serial);
            public string? GetVolumeSerialNumber(string drivePath) => Serial;
            public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber) => volumeSerialNumber == Serial ? "C:\\" : null;
        }

        private (JobScheduler scheduler, TestJobRepository jobRepo, TestHistoryRepository historyRepo, CustomQuartzJobFactory jobFactory, ServiceProvider sp) BuildSchedulerEnvironment()
        {
            var services = new ServiceCollection();
            var jobRepo = new TestJobRepository();
            var historyRepo = new TestHistoryRepository();
            var usbService = new TestUsbDriveService();
            var cpRepo = _checkpointRepository;
            var gate = new JobExecutionGate(cpRepo, _logService);

            services.AddSingleton<IJobRepository>(jobRepo);
            services.AddSingleton<IHistoryRepository>(historyRepo);
            services.AddSingleton<ICheckpointRepository>(cpRepo);
            services.AddSingleton<IJobExecutionGate>(gate);
            services.AddSingleton<IFileCopyService, FileCopyService>();
            services.AddSingleton<IUsbDriveService>(usbService);
            services.AddSingleton<ILogService>(_logService);
            services.AddSingleton<INotificationService, MockNotificationService>();
            services.AddSingleton<IJobExecutionManager, JobExecutionManager>();
            services.AddSingleton<CustomQuartzJobFactory>();
            services.AddTransient<QuartzCopyJob>();

            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<CustomQuartzJobFactory>();
            var scheduler = new JobScheduler(factory, _logService, jobRepo, historyRepo);

            return (scheduler, jobRepo, historyRepo, factory, sp);
        }

        private class MockNotificationService : INotificationService
        {
            public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info) { }
            public void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? errorMessage = null) { }
            public void ShowUsbConnectedNotification(string driveLetter, string volumeLabel) { }
            public void ShowUsbDisconnectedNotification(string driveLetter) { }
        }

        private (string srcDir, string destDir) CreateTestDirectories(string prefix)
        {
            string srcDir = Path.Combine(_testRootDir, prefix + "_src");
            string destDir = Path.Combine(_testRootDir, prefix + "_dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "sample.txt"), "Sample data " + Guid.NewGuid());
            return (srcDir, destDir);
        }

        // TEST A — DAILY FUTURE FIRE AFTER RESTART (Completed today)
        [Fact]
        public async Task TEST_A_DailyCompletedToday_AppRestart_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testA");

            // Daily 08:00, completed today at 08:00
            DateTime today8am = DateTime.Now.Date.AddHours(8);
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job A Daily 0800",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = TimeSpan.FromHours(8) },
                LastRun = today8am,
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(job);
            await historyRepo.AddAsync(new HistoryEntry
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                JobName = job.Name,
                StartTime = today8am,
                EndTime = today8am.AddSeconds(5),
                Status = JobResultStatus.Success,
                FilesCopied = 1
            });

            // Simulate App Startup
            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);

            await Task.Delay(100);

            var historyAfter = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Single(historyAfter); // Only initial pre-existing history entry

            var nextRun = await scheduler.GetNextExecutionTimeAsync(job);
            Assert.NotNull(nextRun);
            Assert.True(nextRun.Value > DateTime.Now);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST B — SECOND DAILY FUTURE FIRE AFTER RESTART (Future today)
        [Fact]
        public async Task TEST_B_DailyFutureToday_AppRestart_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testB");

            // Daily job set for 23:59 tonight (guaranteed future today)
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job B Daily Future",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(23, 59, 59) },
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(job);
            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);

            await Task.Delay(100);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history); // Zero executions

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST C — MULTIPLE JOBS (Completed + Future)
        [Fact]
        public async Task TEST_C_MultipleJobs_AppRestart_ZeroStartupExecutions()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (src1, dest1) = CreateTestDirectories("testC1");
            var (src2, dest2) = CreateTestDirectories("testC2");

            var jobA = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job A 0800 Completed",
                Enabled = true,
                SourcePaths = new List<string> { src1 },
                DestinationPath = dest1,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = TimeSpan.FromHours(8) },
                LastRun = DateTime.Now.Date.AddHours(8),
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            var jobB = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job B 2359 Future",
                Enabled = true,
                SourcePaths = new List<string> { src2 },
                DestinationPath = dest2,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(23, 59, 59) },
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(jobA);
            await jobRepo.AddAsync(jobB);

            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(jobA);
            await scheduler.ScheduleJobAsync(jobB);

            await Task.Delay(150);

            var hA = await historyRepo.GetByJobIdAsync(jobA.Id);
            var hB = await historyRepo.GetByJobIdAsync(jobB.Id);

            Assert.Empty(hA);
            Assert.Empty(hB);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST D — GENUINE MISSED OCCURRENCE + RUN IMMEDIATELY
        [Fact]
        public async Task TEST_D_GenuineMissedOccurrence_RunImmediately_ExecutesOnce()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testD");

            // Daily 00:01 AM today, app was OFF, LastRun is yesterday
            DateTime yesterday = DateTime.Now.Date.AddDays(-1).AddHours(8);
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job D Missed",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(0, 0, 1) },
                LastRun = yesterday,
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(job);
            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);

            // Give time for catch-up execution to finish
            await Task.Delay(800);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Single(history);
            Assert.Equal(JobResultStatus.Success, history[0].Status);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST E — GENUINE MISSED OCCURRENCE + SKIP
        [Fact]
        public async Task TEST_E_GenuineMissedOccurrence_Skip_DoesNotExecute()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testE");

            DateTime yesterday = DateTime.Now.Date.AddDays(-1).AddHours(8);
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job E Missed Skip",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(0, 0, 1) },
                LastRun = yesterday,
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.Skip
            };

            await jobRepo.AddAsync(job);
            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);

            await Task.Delay(200);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history); // Zero executions

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST F — RESTART AGAIN AFTER MISFIRE CATCH-UP
        [Fact]
        public async Task TEST_F_RestartAgainAfterMisfireCatchup_DoesNotExecuteSecondTime()
        {
            var (scheduler1, jobRepo, historyRepo, _, sp1) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testF");

            DateTime yesterday = DateTime.Now.Date.AddDays(-1).AddHours(8);
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job F Catchup",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(0, 0, 1) },
                LastRun = yesterday,
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(job);
            await scheduler1.StartAsync();
            await scheduler1.ScheduleJobAsync(job);
            await Task.Delay(800);

            var history1 = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Single(history1); // First catchup run executed

            await scheduler1.ShutdownAsync();
            sp1.Dispose();

            // RESTART 2: Simulate second app startup
            var (scheduler2, jobRepo2, historyRepo2, _, sp2) = BuildSchedulerEnvironment();

            // Copy over updated job state (with updated LastRun) and history
            var updatedJob = await jobRepo.GetByIdAsync(job.Id);
            Assert.NotNull(updatedJob);
            await jobRepo2.AddAsync(updatedJob!);
            foreach (var h in history1) await historyRepo2.AddAsync(h);

            await scheduler2.StartAsync();
            await scheduler2.ScheduleJobAsync(updatedJob!);
            await Task.Delay(300);

            var history2 = await historyRepo2.GetByJobIdAsync(job.Id);
            Assert.Single(history2); // Still exactly 1 history record! Zero duplicate executions!

            await scheduler2.ShutdownAsync();
            sp2.Dispose();
        }

        // TEST G — FUTURE SCHEDULE REPEATED RESTART 5 TIMES
        [Fact]
        public async Task TEST_G_FutureSchedule_RepeatedRestarts_ExecutionCountRemainsZero()
        {
            var (srcDir, destDir) = CreateTestDirectories("testG");

            for (int i = 0; i < 5; i++)
            {
                var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
                var job = new Job
                {
                    Id = Guid.NewGuid(),
                    Name = "Job G Repeated Restart",
                    Enabled = true,
                    SourcePaths = new List<string> { srcDir },
                    DestinationPath = destDir,
                    CopyMode = CopyMode.Mirror,
                    Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = new TimeSpan(23, 59, 59) },
                    MissedJobBehavior = MissedJobBehavior.RunImmediately
                };

                await jobRepo.AddAsync(job);
                await scheduler.StartAsync();
                await scheduler.ScheduleJobAsync(job);
                await Task.Delay(100);

                var history = await historyRepo.GetByJobIdAsync(job.Id);
                Assert.Empty(history);

                await scheduler.ShutdownAsync();
                sp.Dispose();
            }
        }

        // TEST H — CLEAN EXIT VS PROCESS RESTART
        [Fact]
        public async Task TEST_H_CleanExitFollowedByRestart_DoesNotExecuteCompletedJobs()
        {
            var (scheduler1, jobRepo, historyRepo, _, sp1) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testH");

            DateTime today8am = DateTime.Now.Date.AddHours(8);
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job H Clean Exit",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                Schedule = new JobSchedule { ScheduleType = ScheduleType.Daily, TimeOfDay = TimeSpan.FromHours(8) },
                LastRun = today8am,
                LastResult = JobResultStatus.Success,
                MissedJobBehavior = MissedJobBehavior.RunImmediately
            };

            await jobRepo.AddAsync(job);
            await scheduler1.StartAsync();
            await scheduler1.ScheduleJobAsync(job);
            await scheduler1.ShutdownAsync(); // Clean Exit
            sp1.Dispose();

            // Restart process
            var (scheduler2, jobRepo2, historyRepo2, _, sp2) = BuildSchedulerEnvironment();
            await jobRepo2.AddAsync(job);
            await scheduler2.StartAsync();
            await scheduler2.ScheduleJobAsync(job);
            await Task.Delay(200);

            var history = await historyRepo2.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            await scheduler2.ShutdownAsync();
            sp2.Dispose();
        }

        // TEST I — RECOVERY CHECKPOINT INDEPENDENCE
        [Fact]
        public async Task TEST_I_UnresolvedRecoveryCheckpoint_AppStartup_DoesNotAutoResume()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testI");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Job I Recovery Checkpoint",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror
            };
            await jobRepo.AddAsync(job);

            // Create unresolved recoverable checkpoint
            var cp = new JobCheckpoint
            {
                JobId = job.Id,
                JobName = job.Name,
                CurrentState = ExecutionState.Stopped,
                IsRecoverable = true,
                ProcessInstanceId = Guid.NewGuid()
            };
            await _checkpointRepository.SaveCheckpointAsync(cp);

            // Startup scheduler
            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);
            await Task.Delay(200);

            // Assert: checkpoint is NOT automatically resumed by scheduler startup
            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            var cpAfter = await _checkpointRepository.GetCheckpointAsync(job.Id);
            Assert.NotNull(cpAfter);
            Assert.Equal(ExecutionState.Stopped, cpAfter!.CurrentState);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST J — USB IDENTITY REGRESSION
        [Fact]
        public async Task TEST_J_UsbIdentityValidation_Preserved()
        {
            var (scheduler, jobRepo, _, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testJ");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test J USB Serial",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                TargetVolumeSerialNumber = "VOL-145"
            };
            await jobRepo.AddAsync(job);

            var service = sp.GetRequiredService<IFileCopyService>();
            var res = await service.CopyAsync(job, false, null, CancellationToken.None);

            Assert.True(res.Success);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST K — MIRROR v14.4 REGRESSION
        [Fact]
        public async Task TEST_K_Mirrorv144Equivalence_Preserved()
        {
            var (_, _, _, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testK");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test K Mirror v144",
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                CopyMode = CopyMode.Mirror,
                EnableMirrorDeletion = true,
                ConflictPolicy = ConflictPolicy.Overwrite,
                PreserveTimestamps = true
            };

            var service = sp.GetRequiredService<IFileCopyService>();

            // Run 1
            var res1 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(1, res1.FilesCopied);

            // Run 2 (Identical second pass)
            var res2 = await service.CopyAsync(job, false, null, CancellationToken.None);
            Assert.Equal(0, res2.FilesCopied);
            Assert.Equal(1, res2.FilesSkipped);
            Assert.Equal(0, res2.BytesWrittenThisExecution);

            sp.Dispose();
        }

        // TEST L — ONE-TIME FUTURE SCHEDULE
        [Fact]
        public async Task TEST_L_OneTimeFutureSchedule_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testL");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test L OneTime Future",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.OneTime,
                    StartDate = DateTime.Now.Date.AddDays(2),
                    TimeOfDay = TimeSpan.FromHours(14)
                }
            };
            await jobRepo.AddAsync(job);

            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);
            await Task.Delay(150);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST M — WEEKLY FUTURE SCHEDULE
        [Fact]
        public async Task TEST_M_WeeklyFutureSchedule_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testM");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test M Weekly Future",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Weekly,
                    DaysOfWeek = new bool[] { false, false, false, false, false, false, true }, // Saturday
                    TimeOfDay = new TimeSpan(23, 59, 59)
                }
            };
            await jobRepo.AddAsync(job);

            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);
            await Task.Delay(150);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST N — MONTHLY FUTURE SCHEDULE
        [Fact]
        public async Task TEST_N_MonthlyFutureSchedule_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testN");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test N Monthly Future",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Monthly,
                    DayOfMonth = 28,
                    TimeOfDay = new TimeSpan(23, 59, 59)
                }
            };
            await jobRepo.AddAsync(job);

            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);
            await Task.Delay(150);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }

        // TEST O — CRON FUTURE SCHEDULE
        [Fact]
        public async Task TEST_O_CronFutureSchedule_DoesNotExecuteEarly()
        {
            var (scheduler, jobRepo, historyRepo, _, sp) = BuildSchedulerEnvironment();
            var (srcDir, destDir) = CreateTestDirectories("testO");

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Name = "Test O Cron Future",
                Enabled = true,
                SourcePaths = new List<string> { srcDir },
                DestinationPath = destDir,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Cron,
                    CronExpression = "0 59 23 * * ?"
                }
            };
            await jobRepo.AddAsync(job);

            await scheduler.StartAsync();
            await scheduler.ScheduleJobAsync(job);
            await Task.Delay(150);

            var history = await historyRepo.GetByJobIdAsync(job.Id);
            Assert.Empty(history);

            await scheduler.ShutdownAsync();
            sp.Dispose();
        }
    }
}
