namespace ScheduledCopyManager.Domain.Enums
{
    public enum CheckpointFileStatus
    {
        Pending,
        Copying,
        Completed,
        Skipped,
        Failed
    }
}
