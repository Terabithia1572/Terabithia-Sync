namespace ScheduledCopyManager.Domain.Enums;

/// <summary>
/// Defines the type of schedule for a job.
/// </summary>
public enum ScheduleType
{
    /// <summary>Run once at a specific date/time.</summary>
    OneTime,
    /// <summary>Run daily at a specified time.</summary>
    Daily,
    /// <summary>Run weekly on specified day(s) at a specified time.</summary>
    Weekly,
    /// <summary>Run monthly on specified day of month at a specified time.</summary>
    Monthly,
    /// <summary>Custom cron expression.</summary>
    Cron
}
