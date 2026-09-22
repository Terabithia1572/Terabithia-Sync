using System;
using System.Diagnostics;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class ProgressThrottler
    {
        private readonly IProgress<FileCopyProgress>? _targetProgress;
        private readonly long _throttleIntervalTicks;
        private readonly Stopwatch _totalStopwatch;
        private readonly Stopwatch _activeStopwatch;
        private readonly Stopwatch _pausedStopwatch;
        private long _lastReportTimestamp;

        // Rolling speed calculation window (stores timestamps and bytes)
        private const int RollingWindowSize = 10;
        private readonly long[] _rollingTimestamps = new long[RollingWindowSize];
        private readonly long[] _rollingBytes = new long[RollingWindowSize];
        private int _rollingIndex = 0;
        private int _rollingCount = 0;

        public FileCopyProgress ProgressData { get; }
        public long ValidatedBaselineBytes { get; set; }
        public long SessionCopiedBytes { get; private set; }

        public ProgressThrottler(
            IProgress<FileCopyProgress>? targetProgress,
            Job job,
            int totalFiles,
            long totalBytes,
            long validatedBaselineBytes = 0,
            int throttleIntervalMs = 100)
        {
            _targetProgress = targetProgress;
            _throttleIntervalTicks = (long)(throttleIntervalMs * (Stopwatch.Frequency / 1000.0));
            _totalStopwatch = Stopwatch.StartNew();
            _activeStopwatch = Stopwatch.StartNew();
            _pausedStopwatch = new Stopwatch();

            ValidatedBaselineBytes = validatedBaselineBytes;
            SessionCopiedBytes = 0;

            ProgressData = new FileCopyProgress
            {
                JobId = job.Id,
                JobName = job.Name ?? string.Empty,
                TotalFiles = totalFiles,
                TotalBytes = totalBytes,
                BytesCopied = validatedBaselineBytes,
                State = ExecutionState.Running,
                StatusMessage = "Başlatılıyor..."
            };

            _lastReportTimestamp = 0;
        }

        public void ReportStart()
        {
            ReportInternal(force: true);
        }

        public void ReportChunk(int bytesRead, long currentFileBytesCopied, long currentFileSize)
        {
            if (bytesRead > 0)
            {
                SessionCopiedBytes += bytesRead;
                ProgressData.BytesCopied = ValidatedBaselineBytes + SessionCopiedBytes;
                if (ProgressData.TotalBytes > 0 && ProgressData.BytesCopied > ProgressData.TotalBytes)
                {
                    ProgressData.BytesCopied = ProgressData.TotalBytes;
                }
                AddRollingSample(bytesRead);
            }

            ProgressData.CurrentFileBytesCopied = currentFileBytesCopied;
            ProgressData.CurrentFileSize = currentFileSize;

            UpdateTimingsAndSpeed();

            long now = Stopwatch.GetTimestamp();
            if (_lastReportTimestamp == 0 || (now - _lastReportTimestamp) >= _throttleIntervalTicks)
            {
                ReportInternal(force: false);
            }
        }

        public void ReportFileCompleted(int filesCopied, int filesSkipped, int filesFailed)
        {
            ProgressData.FilesCopied = filesCopied;
            ProgressData.FilesSkipped = filesSkipped;
            ProgressData.FilesFailed = filesFailed;
            ProgressData.FilesPending = Math.Max(0, ProgressData.TotalFiles - (filesCopied + filesSkipped + filesFailed));

            UpdateTimingsAndSpeed();
            ReportInternal(force: true);
        }

        public void ReportState(ExecutionState state, string? statusMessage = null)
        {
            ProgressData.State = state;
            if (statusMessage != null)
            {
                ProgressData.StatusMessage = statusMessage;
            }

            if (state == ExecutionState.Paused || state == ExecutionState.DestinationUnavailable || state == ExecutionState.WaitingForDrive)
            {
                if (_activeStopwatch.IsRunning) _activeStopwatch.Stop();
                if (!_pausedStopwatch.IsRunning) _pausedStopwatch.Start();
                ClearRollingSamples();
            }
            else if (state == ExecutionState.Running)
            {
                if (_pausedStopwatch.IsRunning) _pausedStopwatch.Stop();
                if (!_activeStopwatch.IsRunning) _activeStopwatch.Start();
                ClearRollingSamples();
            }

            UpdateTimingsAndSpeed();
            ReportInternal(force: true);
        }

        public void ReportFinal(bool success, string finalMessage)
        {
            _totalStopwatch.Stop();
            _activeStopwatch.Stop();
            _pausedStopwatch.Stop();

            ProgressData.State = success ? ExecutionState.Completed : ExecutionState.Failed;
            ProgressData.StatusMessage = finalMessage;
            if (success)
            {
                ProgressData.BytesCopied = ProgressData.TotalBytes;
                ProgressData.FilesPending = 0;
            }

            UpdateTimingsAndSpeed();
            ReportInternal(force: true);
        }

        private void ReportInternal(bool force)
        {
            if (_targetProgress == null) return;
            _lastReportTimestamp = Stopwatch.GetTimestamp();

            // Clone progress data snapshot for thread-safe UI reporting
            var snapshot = new FileCopyProgress
            {
                JobId = ProgressData.JobId,
                JobName = ProgressData.JobName,
                CurrentFileName = ProgressData.CurrentFileName,
                CurrentFilePath = ProgressData.CurrentFilePath,
                CurrentFileBytesCopied = ProgressData.CurrentFileBytesCopied,
                CurrentFileSize = ProgressData.CurrentFileSize,
                FilesCopied = ProgressData.FilesCopied,
                FilesSkipped = ProgressData.FilesSkipped,
                FilesFailed = ProgressData.FilesFailed,
                FilesPending = ProgressData.FilesPending,
                TotalFiles = ProgressData.TotalFiles,
                BytesCopied = ProgressData.BytesCopied,
                TotalBytes = ProgressData.TotalBytes,
                BytesPerSecond = ProgressData.BytesPerSecond,
                CurrentBytesPerSecond = ProgressData.CurrentBytesPerSecond,
                AverageBytesPerSecond = ProgressData.AverageBytesPerSecond,
                ElapsedTime = ProgressData.ElapsedTime,
                ActiveCopyDuration = ProgressData.ActiveCopyDuration,
                PausedDuration = ProgressData.PausedDuration,
                RemainingTime = ProgressData.RemainingTime,
                State = ProgressData.State,
                StatusMessage = ProgressData.StatusMessage,
                IsWaitingForUsb = ProgressData.IsWaitingForUsb
            };

            _targetProgress.Report(snapshot);
        }

        private void UpdateTimingsAndSpeed()
        {
            ProgressData.ElapsedTime = _totalStopwatch.Elapsed;
            ProgressData.ActiveCopyDuration = _activeStopwatch.Elapsed;
            ProgressData.PausedDuration = _pausedStopwatch.Elapsed;

            // Average speed calculated strictly from new session bytes transferred
            double activeSec = ProgressData.ActiveCopyDuration.TotalSeconds;
            if (activeSec > 0.001)
            {
                ProgressData.AverageBytesPerSecond = SessionCopiedBytes / activeSec;
            }
            else
            {
                ProgressData.AverageBytesPerSecond = 0;
            }

            if (ProgressData.State == ExecutionState.Paused || ProgressData.State == ExecutionState.DestinationUnavailable || ProgressData.State == ExecutionState.WaitingForDrive)
            {
                ProgressData.CurrentBytesPerSecond = 0;
                ProgressData.BytesPerSecond = 0;
                ProgressData.RemainingTime = TimeSpan.Zero;
                return;
            }

            // Current rolling speed
            ProgressData.CurrentBytesPerSecond = CalculateCurrentRollingSpeed();
            ProgressData.BytesPerSecond = ProgressData.CurrentBytesPerSecond > 0 ? ProgressData.CurrentBytesPerSecond : ProgressData.AverageBytesPerSecond;

            // Stabilized ETA calculation using Remaining Bytes / Current Speed
            bool isDataStabilized = activeSec >= 1.0 && (SessionCopiedBytes >= 131072 || ProgressData.BytesPerSecond > 0);
            if (isDataStabilized && ProgressData.BytesPerSecond > 0 && ProgressData.TotalBytes > ProgressData.BytesCopied)
            {
                long bytesRemaining = ProgressData.TotalBytes - ProgressData.BytesCopied;
                double secondsLeft = bytesRemaining / ProgressData.BytesPerSecond;
                if (double.IsNaN(secondsLeft) || double.IsInfinity(secondsLeft) || secondsLeft < 0)
                {
                    ProgressData.RemainingTime = TimeSpan.Zero;
                }
                else
                {
                    ProgressData.RemainingTime = TimeSpan.FromSeconds(Math.Min(secondsLeft, 864000)); // Cap max 10 days
                }
            }
            else
            {
                ProgressData.RemainingTime = TimeSpan.Zero;
            }
        }

        private void AddRollingSample(long bytes)
        {
            long now = Stopwatch.GetTimestamp();
            _rollingTimestamps[_rollingIndex] = now;
            _rollingBytes[_rollingIndex] = bytes;

            _rollingIndex = (_rollingIndex + 1) % RollingWindowSize;
            if (_rollingCount < RollingWindowSize) _rollingCount++;
        }

        private void ClearRollingSamples()
        {
            Array.Clear(_rollingTimestamps, 0, RollingWindowSize);
            Array.Clear(_rollingBytes, 0, RollingWindowSize);
            _rollingIndex = 0;
            _rollingCount = 0;
        }

        private double CalculateCurrentRollingSpeed()
        {
            if (_rollingCount < 3) return 0;

            long oldestTimestamp = long.MaxValue;
            long newestTimestamp = long.MinValue;
            long totalWindowBytes = 0;

            for (int i = 0; i < _rollingCount; i++)
            {
                long ts = _rollingTimestamps[i];
                if (ts < oldestTimestamp) oldestTimestamp = ts;
                if (ts > newestTimestamp) newestTimestamp = ts;
                totalWindowBytes += _rollingBytes[i];
            }

            double elapsedSec = (newestTimestamp - oldestTimestamp) / (double)Stopwatch.Frequency;
            if (elapsedSec < 0.05) return 0;

            double speed = totalWindowBytes / elapsedSec;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0) return 0;
            return speed;
        }
    }
}
