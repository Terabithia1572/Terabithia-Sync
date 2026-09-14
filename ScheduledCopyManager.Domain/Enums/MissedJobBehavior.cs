namespace ScheduledCopyManager.Domain.Enums;

/// <summary>
/// Defines how to handle a job that missed its scheduled run.
/// </summary>
public enum MissedJobBehavior
{
    /// <summary>Run the missed job as soon as possible.</summary>
    RunImmediately,
    /// <summary>Skip the missed run and wait for the next scheduled time.</summary>
    Skip,
    /// <summary>Reschedule the next run based on the original schedule.</summary>
    Reschedule
}
