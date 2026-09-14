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
    public class HistoryRepository : IHistoryRepository
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;
        private const int MaxHistoryCount = 1000;

        public HistoryRepository(string? customDataDir = null)
        {
            string dataDir = customDataDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Terabithia Sync");

            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _filePath = Path.Combine(dataDir, "history.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            };
        }

        public async Task<IReadOnlyList<HistoryEntry>> GetAllAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                return LoadHistoryInternal();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task AddAsync(HistoryEntry entry)
        {
            await _semaphore.WaitAsync();
            try
            {
                var history = LoadHistoryInternal();
                history.Insert(0, entry);
                if (history.Count > MaxHistoryCount)
                {
                    history = history.Take(MaxHistoryCount).ToList();
                }
                SaveHistoryInternal(history);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task UpdateAsync(HistoryEntry entry)
        {
            await _semaphore.WaitAsync();
            try
            {
                var history = LoadHistoryInternal();
                int idx = history.FindIndex(h => h.Id == entry.Id);
                if (idx >= 0)
                {
                    history[idx] = entry;
                    SaveHistoryInternal(history);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task DeleteAsync(HistoryEntry entry)
        {
            await _semaphore.WaitAsync();
            try
            {
                var history = LoadHistoryInternal();
                history.RemoveAll(h => h.Id == entry.Id);
                SaveHistoryInternal(history);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task ClearAllAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                SaveHistoryInternal(new List<HistoryEntry>());
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private List<HistoryEntry> LoadHistoryInternal()
        {
            if (!File.Exists(_filePath))
                return new List<HistoryEntry>();

            try
            {
                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<HistoryEntry>();

                return JsonSerializer.Deserialize<List<HistoryEntry>>(json, _jsonOptions) ?? new List<HistoryEntry>();
            }
            catch (Exception)
            {
                try
                {
                    string corruptPath = _filePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Copy(_filePath, corruptPath, overwrite: true);
                }
                catch { }

                return new List<HistoryEntry>();
            }
        }

        private void SaveHistoryInternal(List<HistoryEntry> history)
        {
            string tempPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(history, _jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
    }
}
