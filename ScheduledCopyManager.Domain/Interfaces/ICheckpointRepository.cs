using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface ICheckpointRepository
    {
        Task SaveCheckpointAsync(JobCheckpoint checkpoint);
        Task<JobCheckpoint?> GetCheckpointAsync(Guid jobId);
        Task<IReadOnlyList<JobCheckpoint>> GetAllCheckpointsAsync();
        Task<IReadOnlyList<JobCheckpoint>> GetRecoverableCheckpointsAsync();
        Task DeleteCheckpointAsync(Guid jobId);
        Task ClearAllCheckpointsAsync();
    }
}
