using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Repositories
{
    public class JobRepository : IJobRepository
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;

        public JobRepository(string? customDataDir = null)
        {
            string dataDir = customDataDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Terabithia Sync");

            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _filePath = Path.Combine(dataDir, "jobs.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            };
        }

        public async Task<IReadOnlyList<Job>> GetAllAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                return LoadJobsInternal();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<Job?> GetByIdAsync(Guid id)
        {
            await _semaphore.WaitAsync();
            try
            {
                var jobs = LoadJobsInternal();
                return jobs.FirstOrDefault(j => j.Id == id);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task AddAsync(Job job)
        {
            await _semaphore.WaitAsync();
            try
            {
                var jobs = LoadJobsInternal();
                jobs.RemoveAll(j => j.Id == job.Id);
                jobs.Add(job);
                SaveJobsInternal(jobs);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task UpdateAsync(Job job)
        {
            await _semaphore.WaitAsync();
            try
            {
                var jobs = LoadJobsInternal();
                int idx = jobs.FindIndex(j => j.Id == job.Id);
                if (idx >= 0)
                {
                    jobs[idx] = job;
                }
                else
                {
                    jobs.Add(job);
                }
                SaveJobsInternal(jobs);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task DeleteAsync(Guid id)
        {
            await _semaphore.WaitAsync();
            try
            {
                var jobs = LoadJobsInternal();
                int count = jobs.RemoveAll(j => j.Id == id);
                if (count > 0)
                {
                    SaveJobsInternal(jobs);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private List<Job> LoadJobsInternal()
        {
            if (!File.Exists(_filePath))
                return new List<Job>();

            try
            {
                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<Job>();

                return JsonSerializer.Deserialize<List<Job>>(json, _jsonOptions) ?? new List<Job>();
            }
            catch (Exception)
            {
                // Corrupt file recovery strategy: backup corrupt file and start fresh
                try
                {
                    string corruptPath = _filePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Copy(_filePath, corruptPath, overwrite: true);
                }
                catch { }

                return new List<Job>();
            }
        }

        private void SaveJobsInternal(List<Job> jobs)
        {
            string tempPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(jobs, _jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
    }
}
