using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class TokenBucketBandwidthLimiter : IBandwidthLimiter
    {
        private readonly object _lock = new();
        private double _megabytesPerSecond;
        private double _bytesPerSecond;
        private double _maxCapacity;
        private double _availableTokens;
        private long _lastRefillTimestamp;

        public bool IsEnabled => _bytesPerSecond > 0;
        public double MegabytesPerSecond => _megabytesPerSecond;

        public TokenBucketBandwidthLimiter(double megabytesPerSecond)
        {
            SetSpeedLimit(megabytesPerSecond);
        }

        public void SetSpeedLimit(double megabytesPerSecond)
        {
            lock (_lock)
            {
                _megabytesPerSecond = Math.Max(0, megabytesPerSecond);
                _bytesPerSecond = _megabytesPerSecond * 1024.0 * 1024.0;
                // Max capacity capped at 0.05 seconds of throughput or max 256 KB
                // This eliminates startup and post-pause bursts
                _maxCapacity = _bytesPerSecond > 0 ? Math.Min(_bytesPerSecond * 0.05, 262144.0) : 0;
                _availableTokens = 0; // Zero startup burst
                _lastRefillTimestamp = Stopwatch.GetTimestamp();
            }
        }

        public async Task ConsumeAsync(int byteCount, CancellationToken cancellationToken = default)
        {
            if (byteCount <= 0 || !IsEnabled) return;

            cancellationToken.ThrowIfCancellationRequested();
            double waitTimeMs = 0;

            lock (_lock)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
                _lastRefillTimestamp = now;

                if (elapsedSeconds > 0)
                {
                    _availableTokens = Math.Min(_maxCapacity, _availableTokens + (elapsedSeconds * _bytesPerSecond));
                }

                _availableTokens -= byteCount;

                if (_availableTokens < 0)
                {
                    double deficitBytes = -_availableTokens;
                    waitTimeMs = (deficitBytes / _bytesPerSecond) * 1000.0;
                }
            }

            if (waitTimeMs > 0)
            {
                int delayMs = (int)Math.Ceiling(waitTimeMs);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
        }
    }
}

