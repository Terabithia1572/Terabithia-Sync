using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class RetryPolicy
    {
        public bool Enabled { get; set; } = true;
        public int MaxAttempts { get; set; } = 3;
        public int DelaySeconds { get; set; } = 5;
        public bool UseExponentialBackoff { get; set; } = true;
        public bool RetryLockedFiles { get; set; } = true;
        public bool WaitForDestination { get; set; } = true;
        public bool ResumeWhenDestinationReturns { get; set; } = true;
        public FailureBehavior FailureBehavior { get; set; } = FailureBehavior.ContinueWithRemaining;
    }
}
