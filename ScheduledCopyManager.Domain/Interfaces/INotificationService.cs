using System;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface INotificationService
    {
        void ShowNotification(string title, string message, NotificationType type = NotificationType.Info);
        void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? message = null);
    }

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }
}
