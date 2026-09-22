using System.Threading;
using System.Threading.Tasks;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IPauseToken
    {
        bool IsPaused { get; }
        Task WaitWhilePausedAsync(CancellationToken cancellationToken = default);
    }
}
