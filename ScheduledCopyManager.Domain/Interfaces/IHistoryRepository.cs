using System.Collections.Generic;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IHistoryRepository
    {
        Task<IReadOnlyList<HistoryEntry>> GetAllAsync();
        Task AddAsync(HistoryEntry entry);
        Task UpdateAsync(HistoryEntry entry);
        Task DeleteAsync(HistoryEntry entry);
        Task ClearAllAsync();
    }
}
