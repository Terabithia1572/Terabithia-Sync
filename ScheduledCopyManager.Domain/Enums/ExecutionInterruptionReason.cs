namespace ScheduledCopyManager.Domain.Enums
{
    public enum ExecutionInterruptionReason
    {
        None = 0,
        UnexpectedProcessExit = 1,
        ApplicationCrash = 2,
        WindowsShutdown = 3,
        DestinationUnavailable = 4,
        UserPaused = 5,
        UserStopped = 6,
        UserCancelled = 7,
        Failure = 8
    }
}
