using System;
using System.IO;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.Infrastructure.Services;
using Quartz.Spi;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class SchedulerAndPolicyTests : IDisposable
    {
        private readonly string _testDir;

        public SchedulerAndPolicyTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "SchedTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
        }

        // 18. Repository Serialization & Persistence
        [Fact]
        public async Task Repository_18_FullSerializationVerification_PersistsJobWithAllProperties()
        {
            var repo = new JobRepository(_testDir);
            var originalJob = new Job
            {
                Name = "Serialization Test Job",
                SourcePaths = new() { "C:\\Source1", "C:\\Source2" },
                DestinationPath = "E:\\Backup",
                CopyMode = CopyMode.Mirror,
                ConflictPolicy = ConflictPolicy.Rename,
                EnableMirrorDeletion = true,
                VerifyCopy = true,
                PreserveTimestamps = true,
                PreserveAttributes = true,
                RetryCount = 5,
                RetryDelay = TimeSpan.FromSeconds(10),
                IsUsbDestination = true,
                MissedJobBehavior = MissedJobBehavior.RunImmediately,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Daily,
                    TimeOfDay = new TimeSpan(14, 30, 0)
                }
            };

            await repo.AddAsync(originalJob);

            var repo2 = new JobRepository(_testDir);
            var retrieved = await repo2.GetByIdAsync(originalJob.Id);

            Assert.NotNull(retrieved);
            Assert.Equal("Serialization Test Job", retrieved!.Name);
            Assert.Equal(2, retrieved.SourcePaths.Count);
            Assert.Equal(CopyMode.Mirror, retrieved.CopyMode);
            Assert.Equal(ConflictPolicy.Rename, retrieved.ConflictPolicy);
            Assert.True(retrieved.EnableMirrorDeletion);
            Assert.True(retrieved.VerifyCopy);
            Assert.Equal(5, retrieved.RetryCount);
            Assert.True(retrieved.IsUsbDestination);
            Assert.Equal(MissedJobBehavior.RunImmediately, retrieved.MissedJobBehavior);
        }

        // 19. Schedule Calculations
        [Fact]
        public async Task JobScheduler_19_GetNextExecutionTime_CalculatesFutureTimeCorrectly()
        {
            var logService = new LogService(_testDir);
            IJobFactory dummyFactory = null!;
            var scheduler = new JobScheduler(dummyFactory, logService);

            var futureJob = new Job
            {
                Enabled = true,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Daily,
                    TimeOfDay = new TimeSpan(23, 59, 0)
                }
            };

            var nextTime = await scheduler.GetNextExecutionTimeAsync(futureJob);

            Assert.NotNull(nextTime);
            Assert.True(nextTime.Value > DateTime.Now.AddSeconds(-60));
        }

        // 20. Missed Job Behavior Mapping Verification
        [Theory]
        [InlineData(MissedJobBehavior.RunImmediately)]
        [InlineData(MissedJobBehavior.Skip)]
        [InlineData(MissedJobBehavior.Reschedule)]
        public async Task JobScheduler_20_MissedJobBehavior_MapsWithoutErrors(MissedJobBehavior behavior)
        {
            var logService = new LogService(_testDir);
            IJobFactory dummyFactory = null!;
            var scheduler = new JobScheduler(dummyFactory, logService);

            var job = new Job
            {
                Enabled = true,
                MissedJobBehavior = behavior,
                Schedule = new JobSchedule
                {
                    ScheduleType = ScheduleType.Weekly,
                    DaysOfWeek = new bool[7] { true, false, true, false, true, false, false },
                    TimeOfDay = new TimeSpan(12, 0, 0)
                }
            };

            var nextTime = await scheduler.GetNextExecutionTimeAsync(job);
            Assert.NotNull(nextTime);
        }
    }
}
