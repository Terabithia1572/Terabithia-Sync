using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Domain.Models
{
    public class PauseTokenSource
    {
        private readonly object _lock = new();
        private TaskCompletionSource<bool>? _pausedTcs;

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _pausedTcs != null;
                }
            }
        }

        public PauseToken Token => new PauseToken(this);

        public void Pause()
        {
            lock (_lock)
            {
                _pausedTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource<bool>? tcsToResume = null;
            lock (_lock)
            {
                if (_pausedTcs != null)
                {
                    tcsToResume = _pausedTcs;
                    _pausedTcs = null;
                }
            }
            tcsToResume?.TrySetResult(true);
        }

        public async Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                tcs = _pausedTcs;
            }

            if (tcs != null)
            {
                CancellationTokenRegistration registration = default;
                try
                {
                    if (cancellationToken.CanBeCanceled)
                    {
                        registration = cancellationToken.Register(
                            state => ((TaskCompletionSource<bool>?)state)?.TrySetCanceled(cancellationToken),
                            tcs,
                            useSynchronizationContext: false);
                    }

                    await tcs.Task;
                }
                finally
                {
                    registration.Dispose();
                }
            }
        }
    }

    public readonly struct PauseToken : IPauseToken
    {
        private readonly PauseTokenSource? _source;

        public PauseToken(PauseTokenSource? source)
        {
            _source = source;
        }

        public bool IsPaused => _source?.IsPaused ?? false;

        public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
        {
            return _source?.WaitWhilePausedAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
