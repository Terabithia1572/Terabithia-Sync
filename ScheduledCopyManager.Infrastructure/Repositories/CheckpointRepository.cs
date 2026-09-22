using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Repositories
{
    public class CheckpointRepository : ICheckpointRepository
    {
        private readonly string _checkpointDirectory;
        private readonly ILogService? _logService;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public CheckpointRepository(ILogService? logService = null, string? customDirectory = null)
        {
            _logService = logService;

            if (!string.IsNullOrWhiteSpace(customDirectory))
            {
                _checkpointDirectory = customDirectory;
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _checkpointDirectory = Path.Combine(localAppData, "Terabithia Sync", "checkpoints");
            }

            EnsureDirectoryExists();
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_checkpointDirectory))
            {
                Directory.CreateDirectory(_checkpointDirectory);
            }
        }

        private string GetFilePath(Guid jobId) => Path.Combine(_checkpointDirectory, $"{jobId}.checkpoint.json");
        private string GetTempFilePath(Guid jobId) => Path.Combine(_checkpointDirectory, $"{jobId}.checkpoint.json.tmp");
        private string GetCorruptFilePath(Guid jobId) => Path.Combine(_checkpointDirectory, $"{jobId}.checkpoint.json.corrupt");

        public async Task SaveCheckpointAsync(JobCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));

            EnsureDirectoryExists();
            checkpoint.UpdatedAt = DateTime.Now;

            string targetPath = GetFilePath(checkpoint.JobId);
            string tempPath = GetTempFilePath(checkpoint.JobId);

            try
            {
                string json = JsonSerializer.Serialize(checkpoint, JsonOptions);

                // Safe atomic write strategy
                await File.WriteAllTextAsync(tempPath, json);

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, null);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
            }
            catch (Exception ex)
            {
                _logService?.LogError($"Checkpoint kaydedilirken hata oluştu ({checkpoint.JobId}): {ex.Message}");
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }

        public async Task<JobCheckpoint?> GetCheckpointAsync(Guid jobId)
        {
            EnsureDirectoryExists();
            string filePath = GetFilePath(jobId);
            if (!File.Exists(filePath)) return null;

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    IsolateCorruptFile(filePath, jobId, "Dosya boş");
                    return null;
                }

                var checkpoint = JsonSerializer.Deserialize<JobCheckpoint>(json, JsonOptions);
                if (checkpoint == null || checkpoint.SchemaVersion < 1)
                {
                    IsolateCorruptFile(filePath, jobId, "Geçersiz şema sürümü veya yapısı");
                    return null;
                }

                return checkpoint;
            }
            catch (Exception ex)
            {
                IsolateCorruptFile(filePath, jobId, ex.Message);
                return null;
            }
        }

        public async Task<IReadOnlyList<JobCheckpoint>> GetAllCheckpointsAsync()
        {
            EnsureDirectoryExists();
            var result = new List<JobCheckpoint>();

            var files = Directory.EnumerateFiles(_checkpointDirectory, "*.checkpoint.json");
            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                // Extract GUID from filename e.g. "guid.checkpoint"
                string idStr = fileName.Replace(".checkpoint", "");
                if (Guid.TryParse(idStr, out Guid jobId))
                {
                    var cp = await GetCheckpointAsync(jobId);
                    if (cp != null)
                    {
                        result.Add(cp);
                    }
                }
            }

            return result.AsReadOnly();
        }

        public async Task<IReadOnlyList<JobCheckpoint>> GetRecoverableCheckpointsAsync()
        {
            var all = await GetAllCheckpointsAsync();
            return all.Where(cp => cp.IsRecoverable &&
                                   (cp.CurrentState == ExecutionState.Running ||
                                    cp.CurrentState == ExecutionState.Paused ||
                                    cp.CurrentState == ExecutionState.Stopped ||
                                    cp.CurrentState == ExecutionState.Cancelled ||
                                    cp.CurrentState == ExecutionState.Failed ||
                                    cp.DestinationWasUnavailable) &&
                                   (cp.PendingFiles > 0 || cp.FailedFiles > 0 || cp.CompletedFiles < cp.TotalFiles))
                      .GroupBy(cp => cp.JobId)
                      .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
                      .ToList()
                      .AsReadOnly();
        }

        public Task DeleteCheckpointAsync(Guid jobId)
        {
            EnsureDirectoryExists();
            string path = GetFilePath(jobId);
            string tmp = GetTempFilePath(jobId);

            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"Checkpoint silinirken hata ({jobId}): {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task ClearAllCheckpointsAsync()
        {
            EnsureDirectoryExists();
            var files = Directory.EnumerateFiles(_checkpointDirectory, "*.checkpoint.*");
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
            return Task.CompletedTask;
        }

        private void IsolateCorruptFile(string filePath, Guid jobId, string reason)
        {
            string corruptPath = GetCorruptFilePath(jobId);
            _logService?.LogWarning($"Bozuk checkpoint dosyası tespit edildi ve karantinaya alındı: '{filePath}' - Neden: {reason}");

            try
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
                File.Move(filePath, corruptPath);
            }
            catch (Exception ex)
            {
                _logService?.LogError($"Bozuk checkpoint karantinaya alınırken hata: {ex.Message}");
            }
        }
    }
}
