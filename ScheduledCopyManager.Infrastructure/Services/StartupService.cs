using System;
using System.Diagnostics;
using Microsoft.Win32;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class StartupService : IStartupService
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "TerabithiaSync";

        public bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
                if (key == null) return false;
                var value = key.GetValue(AppName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }

        public void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
                if (key == null) return;

                if (enable)
                {
                    string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\" --autostart");
                    }
                }
                else
                {
                    if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set startup: {ex.Message}");
            }
        }
    }
}
