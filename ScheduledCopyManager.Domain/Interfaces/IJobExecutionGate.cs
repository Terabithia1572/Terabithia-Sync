using System;
using System.Threading.Tasks;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public enum ExecutionTriggerSource
    {
        ManualRun,
        RecoveryResume,
        QuartzScheduled,
        QuartzMisfire,
        Retry,
        DestinationReturnedSameActiveSession,
        DestinationReturnedAfterRestart,
        StartupRecovery,
        HistoryRetry,
        HistoryRetrySelected
    }

    public record ExecutionGateResult(bool Allowed, string Reason);

    public interface IJobExecutionGate
    {
        Task<ExecutionGateResult> CanExecuteAsync(Guid jobId, ExecutionTriggerSource source);
    }
}
