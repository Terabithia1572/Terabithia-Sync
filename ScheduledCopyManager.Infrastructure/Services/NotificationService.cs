using System;
using Microsoft.Toolkit.Uwp.Notifications;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class NotificationService : INotificationService
    {
        private readonly ISettingsRepository? _settingsRepository;

        public NotificationService(ISettingsRepository? settingsRepository = null)
        {
            _settingsRepository = settingsRepository;
        }

        private static readonly bool IsRunningInTestHost = IsTestEnvironment();

        private static bool IsTestEnvironment()
        {
            try
            {
                string friendlyName = AppDomain.CurrentDomain.FriendlyName;
                string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                return friendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                       processName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                       friendlyName.Contains("xunit", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
        {
            try
            {
                if (IsRunningInTestHost)
                {
                    System.Diagnostics.Debug.WriteLine($"[TEST SUPPRESSED TOAST] Title: {title}, Message: {message}");
                    return;
                }

                if (_settingsRepository != null)
                {
                    var settings = _settingsRepository.GetAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    if (settings != null)
                    {
                        if (!settings.EnableNotifications) return;

                        if (type == NotificationType.Success && !settings.NotifyOnSuccess) return;
                        if (type == NotificationType.Error && !settings.NotifyOnFailure) return;
                        if (type == NotificationType.Warning && !settings.NotifyOnDestinationUnavailable && !settings.NotifyOnRecoveryWaiting) return;
                    }
                }

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
                if (_settingsRepository != null)
                {
                    var settings = _settingsRepository.GetAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    if (settings != null)
                    {
                        if (!settings.EnableNotifications) return;
                        if (success && !settings.NotifyOnSuccess) return;
                        if (!success && !settings.NotifyOnFailure) return;
                    }
                }

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
