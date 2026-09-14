using System;
using Microsoft.Toolkit.Uwp.Notifications;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class NotificationService : INotificationService
    {
        public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message)
                    .Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification fallback: {ex.Message}");
            }
        }

        public void ShowJobResultNotification(string jobName, bool success, int filesCopied, int filesFailed, string? message = null)
        {
            try
            {
                string title = success
                    ? $"Terabithia Sync - '{jobName}' Başarılı"
                    : $"Terabithia Sync - '{jobName}' Hata";

                string body = success
                    ? $"{filesCopied} dosya kopyalandı."
                    : $"{filesCopied} kopyalandı, {filesFailed} başarısız. {(string.IsNullOrEmpty(message) ? "" : message)}";

                ShowNotification(title, body, success ? NotificationType.Success : NotificationType.Error);
            }
            catch { }
        }
    }
}
