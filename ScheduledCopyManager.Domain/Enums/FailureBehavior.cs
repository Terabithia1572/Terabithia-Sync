namespace ScheduledCopyManager.Domain.Enums
{
    public enum FailureBehavior
    {
        ContinueWithRemaining = 0,
        PauseJob = 1,
        StopJob = 2
    }
}
