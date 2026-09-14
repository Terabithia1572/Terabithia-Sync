namespace ScheduledCopyManager.Domain.Enums
{
    public enum FileItemStatus
    {
        Pending,
        Copying,
        Completed,
        Skipped,
        Failed,
        Retrying,
        WaitingForDestination,
        Cancelled
    }
}
