using System;
using System.IO;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class RepositoryTests : IDisposable
    {
        private readonly string _dataDir;

        public RepositoryTests()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "RepoTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dataDir))
            {
                try { Directory.Delete(_dataDir, true); } catch { }
            }
        }

        [Fact]
        public async Task JobRepository_AddGetUpdateDelete_WorksCorrectly()
        {
            var repo = new JobRepository(_dataDir);
            var job = new Job
            {
                Name = "Daily Backup",
                DestinationPath = "C:\\Backup",
                Enabled = true
            };

            // Add
            await repo.AddAsync(job);
            var all = await repo.GetAllAsync();
            Assert.Single(all);

            // Update
            job.Name = "Daily Backup Updated";
            await repo.UpdateAsync(job);
            var fetched = await repo.GetByIdAsync(job.Id);
            Assert.NotNull(fetched);
            Assert.Equal("Daily Backup Updated", fetched!.Name);

            // Delete
            await repo.DeleteAsync(job.Id);
            var afterDelete = await repo.GetAllAsync();
            Assert.Empty(afterDelete);
        }

        [Fact]
        public async Task HistoryRepository_AddGetAllClear_WorksCorrectly()
        {
            var repo = new HistoryRepository(_dataDir);
            var entry = new HistoryEntry
            {
                JobName = "Test Job",
                Status = JobResultStatus.Success,
                FilesCopied = 10,
                BytesCopied = 5000
            };

            await repo.AddAsync(entry);
            var all = await repo.GetAllAsync();
            Assert.Single(all);
            Assert.Equal("Test Job", all[0].JobName);

            await repo.ClearAllAsync();
            var afterClear = await repo.GetAllAsync();
            Assert.Empty(afterClear);
        }

        [Fact]
        public async Task SettingsRepository_SaveAndRetrieve_WorksCorrectly()
        {
            var repo = new SettingsRepository(_dataDir);
            var settings = await repo.GetAsync();
            Assert.NotNull(settings);

            settings.DefaultRetryCount = 5;
            settings.DefaultConflictPolicy = ConflictPolicy.Rename;
            await repo.SaveAsync(settings);

            var retrieved = await repo.GetAsync();
            Assert.Equal(5, retrieved.DefaultRetryCount);
            Assert.Equal(ConflictPolicy.Rename, retrieved.DefaultConflictPolicy);
        }

        [Fact]
        public async Task Repositories_StartWithMissingOrEmptyFiles_RecoversGracefully()
        {
            string nonExistentDir = Path.Combine(_dataDir, "NewSubFolder");
            var jobRepo = new JobRepository(nonExistentDir);
            var jobs = await jobRepo.GetAllAsync();
            Assert.NotNull(jobs);
            Assert.Empty(jobs);

            var histRepo = new HistoryRepository(nonExistentDir);
            var history = await histRepo.GetAllAsync();
            Assert.NotNull(history);
            Assert.Empty(history);

            var settingsRepo = new SettingsRepository(nonExistentDir);
            var settings = await settingsRepo.GetAsync();
            Assert.NotNull(settings);
            Assert.False(string.IsNullOrEmpty(settings.DataDirectory));
        }

        [Fact]
        public async Task JobRepository_CorruptJsonFile_RecoversGracefully()
        {
            string jobsPath = Path.Combine(_dataDir, "jobs.json");
            await File.WriteAllTextAsync(jobsPath, "{ corrupt json syntax error ...");

            var repo = new JobRepository(_dataDir);
            var jobs = await repo.GetAllAsync();
            Assert.NotNull(jobs);
            Assert.Empty(jobs);
        }
    }
}
