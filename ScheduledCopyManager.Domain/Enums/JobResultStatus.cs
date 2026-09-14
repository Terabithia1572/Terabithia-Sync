namespace ScheduledCopyManager.Domain.Enums;

/// <summary>
/// Represents the final status of a copy job execution.
/// </summary>
public enum JobResultStatus
{
    Success,
    PartialSuccess,
    Failure,
    Cancelled,
    Skipped
}
