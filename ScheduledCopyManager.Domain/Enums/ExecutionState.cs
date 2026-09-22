namespace ScheduledCopyManager.Domain.Enums
{
    public enum ExecutionState
    {
        Idle = 0,
        Running = 1,
        Paused = 2,
        WaitingForDrive = 3,
        Stopped = 4,
        Cancelled = 5,
        Completed = 6,
        Failed = 7,
        DestinationUnavailable = 8
    }
}
