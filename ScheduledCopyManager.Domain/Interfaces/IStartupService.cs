namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IStartupService
    {
        bool IsStartupEnabled();
        void SetStartup(bool enable);
    }
}
