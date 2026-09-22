using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class JobExecutionGate : IJobExecutionGate
    {
        public static readonly Guid CurrentProcessInstanceId = Guid.NewGuid();
        private readonly ICheckpointRepository _checkpointRepository;
        private readonly IJobExecutionManager? _jobExecutionManager;
        private readonly ILogService? _logService;

        public JobExecutionGate(ICheckpointRepository checkpointRepository, ILogService? logService = null, IJobExecutionManager? jobExecutionManager = null)
        {
            _checkpointRepository = checkpointRepository ?? throw new ArgumentNullException(nameof(checkpointRepository));
            _logService = logService;
            _jobExecutionManager = jobExecutionManager;
        }

        public async Task<ExecutionGateResult> CanExecuteAsync(Guid jobId, ExecutionTriggerSource source)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            // Same-Job Mutual Exclusion: Block duplicate execution if job is already active in memory
            if (_jobExecutionManager != null && _jobExecutionManager.IsJobActive(jobId))
            {
                _logService?.LogWarning($"[EXECUTION OWNERSHIP] JobId={jobId}, Source={source}, Decision=BLOCKED (Job is currently active in memory)");
                return new ExecutionGateResult(false, "Bu görev için başka bir kopyalama işlemi zaten çalışıyor.");
            }

            // Rule 1: StartupDiscovery / StartupRecovery / Restart triggers MUST NEVER launch execution automatically.
            if (source == ExecutionTriggerSource.StartupRecovery || source == ExecutionTriggerSource.DestinationReturnedAfterRestart)
            {
                _logService?.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [ProcessInstanceId:{CurrentProcessInstanceId}] [TID:{tid}] GATE EVALUATION: JobId={jobId}, Source={source}, Decision=DISCOVER_ONLY (Automatic startup/restart recovery execution forbidden)");
                return new ExecutionGateResult(false, "Automatic startup recovery execution forbidden by product policy.");
            }

            var cp = await _checkpointRepository.GetCheckpointAsync(jobId);

            if (cp != null && cp.IsRecoverable && cp.CurrentState != ExecutionState.Completed)
            {
                var reasonCode = cp.GetEffectiveInterruptionReason();
                bool isSameProcessSession = cp.ProcessInstanceId.HasValue && cp.ProcessInstanceId.Value == CurrentProcessInstanceId;

                // Rule 2: Explicit Recovery Resume or History Retry requested by user is ALLOWED to take ownership of existing checkpoint.
                if (source == ExecutionTriggerSource.RecoveryResume || source == ExecutionTriggerSource.HistoryRetry || source == ExecutionTriggerSource.HistoryRetrySelected)
                {
                    _logService?.LogInformation($"[EXECUTION OWNERSHIP] JobId={jobId}, Name='{cp.JobName}', Source={source}, CheckpointState={cp.CurrentState}, InterruptionReason={reasonCode}, Decision=ALLOWED (Explicit Recovery Resume / History Retry)");
                    return new ExecutionGateResult(true, "Explicit recovery resume or history retry allowed.");
                }

                // Rule 3: Same-session active USB return is allowed ONLY if originally created in the SAME process run.
                if (isSameProcessSession &&
                    source == ExecutionTriggerSource.DestinationReturnedSameActiveSession &&
                    reasonCode == ExecutionInterruptionReason.DestinationUnavailable &&
                    reasonCode != ExecutionInterruptionReason.UserStopped &&
                    reasonCode != ExecutionInterruptionReason.UserPaused &&
                    reasonCode != ExecutionInterruptionReason.UserCancelled)
                {
                    _logService?.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [ProcessInstanceId:{CurrentProcessInstanceId}] [TID:{tid}] GATE EVALUATION: JobId={jobId}, Name='{cp.JobName}', Source={source}, CheckpointState={cp.CurrentState}, InterruptionReason={reasonCode}, Decision=ALLOWED (Same-session active USB return)");
                    return new ExecutionGateResult(true, "Same-session active USB return allowed.");
                }

                // Rule 4: ALL other trigger sources (QuartzScheduled, QuartzMisfire, ManualRun, Retry, etc.) are BLOCKED.
                _logService?.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [ProcessInstanceId:{CurrentProcessInstanceId}] [TID:{tid}] GATE EVALUATION: JobId={jobId}, Name='{cp.JobName}', Source={source}, CheckpointState={cp.CurrentState}, InterruptionReason={reasonCode}, IsSameProcessSession={isSameProcessSession}, Decision=BLOCKED (Unfinished recoverable checkpoint exists)");
                return new ExecutionGateResult(false, $"Blocked by unfinished recoverable checkpoint (State: {cp.CurrentState}, Reason: {reasonCode}, Source: {source}, SameProcessSession: {isSameProcessSession})");
            }

            _logService?.LogInformation($"[EXECUTION OWNERSHIP] JobId={jobId}, Source={source}, Decision=ALLOWED (No blocking checkpoint or active session)");
            return new ExecutionGateResult(true, "No blocking checkpoint found.");
        }
    }
}
