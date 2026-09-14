using System;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IJobScheduler
    {
        Task StartAsync();
        Task ShutdownAsync();
        Task ScheduleJobAsync(Job job);
        Task UnscheduleJobAsync(Guid jobId);
        Task RescheduleJobAsync(Job job);
        Task TriggerJobNowAsync(Guid jobId, bool dryRun = false);
        Task<DateTime?> GetNextExecutionTimeAsync(Job job);
    }
}
