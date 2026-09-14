using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using Serilog.Formatting.Display;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class LogService : ILogService
    {
        private readonly Serilog.ILogger _logger;
        private readonly string _logDir;
        private readonly ConcurrentQueue<LogEntry> _recentLogs = new();
        private const int MaxInMemoryLogs = 500;

        public LogService(string? customLogDir = null)
        {
            _logDir = customLogDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Terabithia Sync",
                "Logs");

            if (!Directory.Exists(_logDir))
            {
                Directory.CreateDirectory(_logDir);
            }

            string logFilePath = Path.Combine(_logDir, "log-.txt");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            LogInformation("Terabithia Sync LogService başlatıldı.");
        }

        public void LogInformation(string message)
        {
            _logger.Information("{Message}", message);
            AddRecentLog("BİLGİ", message, null);
        }

        public void LogWarning(string message)
        {
            _logger.Warning("{Message}", message);
            AddRecentLog("UYARI", message, null);
        }

        public void LogError(string message, Exception? exception = null)
        {
            if (exception != null)
            {
                _logger.Error(exception, "{Message}", message);
            }
            else
            {
                _logger.Error("{Message}", message);
            }

            AddRecentLog("HATA", message, exception?.ToString());
        }

        public IReadOnlyList<LogEntry> GetRecentLogs(int count = 200)
        {
            return _recentLogs.Reverse().Take(count).ToList();
        }

        public string GetLogDirectory()
        {
            return _logDir;
        }

        public void ClearLogs()
        {
            while (_recentLogs.TryDequeue(out _)) { }
        }

        private void AddRecentLog(string level, string message, string? exception)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                Exception = exception
            };

            _recentLogs.Enqueue(entry);

            while (_recentLogs.Count > MaxInMemoryLogs)
            {
                _recentLogs.TryDequeue(out _);
            }
        }
    }
}
