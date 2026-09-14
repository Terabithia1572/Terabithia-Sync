using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Repositories
{
    public class SettingsRepository : ISettingsRepository
    {
        private readonly string _filePath;
        private readonly string _dataDir;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;

        public SettingsRepository(string? customDataDir = null)
        {
            _dataDir = customDataDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Terabithia Sync");

            if (!Directory.Exists(_dataDir))
            {
                Directory.CreateDirectory(_dataDir);
            }

            _filePath = Path.Combine(_dataDir, "settings.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            };
        }

        public async Task<Settings> GetAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                return LoadSettingsInternal();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveAsync(Settings settings)
        {
            await _semaphore.WaitAsync();
            try
            {
                SaveSettingsInternal(settings);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private Settings LoadSettingsInternal()
        {
            if (!File.Exists(_filePath))
            {
                var defaultSettings = CreateDefaultSettings();
                SaveSettingsInternal(defaultSettings);
                return defaultSettings;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    var defaults = CreateDefaultSettings();
                    SaveSettingsInternal(defaults);
                    return defaults;
                }

                var settings = JsonSerializer.Deserialize<Settings>(json, _jsonOptions) ?? CreateDefaultSettings();
                EnsurePathsSet(settings);
                return settings;
            }
            catch (Exception)
            {
                try
                {
                    string corruptPath = _filePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Copy(_filePath, corruptPath, overwrite: true);
                }
                catch { }

                var defaults = CreateDefaultSettings();
                SaveSettingsInternal(defaults);
                return defaults;
            }
        }

        private void SaveSettingsInternal(Settings settings)
        {
            EnsurePathsSet(settings);
            string tempPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }

        private Settings CreateDefaultSettings()
        {
            var settings = new Settings();
            EnsurePathsSet(settings);
            return settings;
        }

        private void EnsurePathsSet(Settings settings)
        {
            if (string.IsNullOrEmpty(settings.DataDirectory))
            {
                settings.DataDirectory = _dataDir;
            }
            if (string.IsNullOrEmpty(settings.LogDirectory))
            {
                settings.LogDirectory = Path.Combine(_dataDir, "Logs");
            }
        }
    }
}
