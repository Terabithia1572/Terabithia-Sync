using System.Collections.Generic;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces;

/// <summary>
/// Repository for persisting Job objects.
/// </summary>
public interface IJobRepository
{
    Task<IReadOnlyList<Job>> GetAllAsync();
    Task<Job?> GetByIdAsync(Guid id);
    Task AddAsync(Job job);
    Task UpdateAsync(Job job);
    Task DeleteAsync(Guid id);
}
