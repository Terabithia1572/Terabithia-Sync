using System.Threading;
using System.Threading.Tasks;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IBandwidthLimiter
    {
        bool IsEnabled { get; }
        double MegabytesPerSecond { get; }
        Task ConsumeAsync(int byteCount, CancellationToken cancellationToken = default);
    }
}
