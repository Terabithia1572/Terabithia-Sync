using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly IStartupService _startupService;
        private readonly Services.IDialogService _dialogService;
        private readonly ILogService _logService;

        private Settings _currentSettings = new();

        [ObservableProperty] private bool _startWithWindows;
        [ObservableProperty] private bool _startMinimized;
        [ObservableProperty] private bool _enableTrayIcon;
        [ObservableProperty] private bool _closeToTray;
        [ObservableProperty] private bool _enableNotifications;
        [ObservableProperty] private int _defaultRetryCount;
        [ObservableProperty] private int _defaultRetryDelaySeconds;
        [ObservableProperty] private ConflictPolicy _defaultConflictPolicy;
        [ObservableProperty] private int _maxConcurrentJobs;
        [ObservableProperty] private string _logDirectory = string.Empty;
        [ObservableProperty] private string _dataDirectory = string.Empty;

        public Array ConflictPolicies => Enum.GetValues(typeof(ConflictPolicy));

        public SettingsViewModel(
            ISettingsRepository settingsRepository,
            IStartupService startupService,
            Services.IDialogService dialogService,
            ILogService logService)
        {
            _settingsRepository = settingsRepository;
            _startupService = startupService;
            _dialogService = dialogService;
            _logService = logService;
        }

        public async Task InitializeAsync()
        {
            _currentSettings = await _settingsRepository.GetAsync();

            StartWithWindows = _currentSettings.StartWithWindows;
            StartMinimized = _currentSettings.StartMinimized;
            EnableTrayIcon = _currentSettings.EnableTrayIcon;
            CloseToTray = _currentSettings.CloseToTray;
            EnableNotifications = _currentSettings.EnableNotifications;
            DefaultRetryCount = _currentSettings.DefaultRetryCount;
            DefaultRetryDelaySeconds = _currentSettings.DefaultRetryDelaySeconds;
            DefaultConflictPolicy = _currentSettings.DefaultConflictPolicy;
            MaxConcurrentJobs = _currentSettings.MaxConcurrentJobs;
            LogDirectory = _currentSettings.LogDirectory;
            DataDirectory = _currentSettings.DataDirectory;
        }

        [RelayCommand]
        public async Task SaveAsync()
        {
            try
            {
                _currentSettings.StartWithWindows = StartWithWindows;
                _currentSettings.StartMinimized = StartMinimized;
                _currentSettings.EnableTrayIcon = EnableTrayIcon;
                _currentSettings.CloseToTray = CloseToTray;
                _currentSettings.EnableNotifications = EnableNotifications;
                _currentSettings.DefaultRetryCount = Math.Max(1, DefaultRetryCount);
                _currentSettings.DefaultRetryDelaySeconds = Math.Max(1, DefaultRetryDelaySeconds);
                _currentSettings.DefaultConflictPolicy = DefaultConflictPolicy;
                _currentSettings.MaxConcurrentJobs = Math.Max(1, MaxConcurrentJobs);

                await _settingsRepository.SaveAsync(_currentSettings);
                _startupService.SetStartup(StartWithWindows);

                await _dialogService.ShowMessageAsync("Ayarlar Kaydedildi", "Uygulama ayarları başarıyla güncellendi.");
                _logService.LogInformation("Uygulama ayarları kaydedildi.");
            }
            catch (Exception ex)
            {
                _logService.LogError("Ayarlar kaydedilirken hata", ex);
                await _dialogService.ShowMessageAsync("Hata", $"Ayarlar kaydedilemedi: {ex.Message}");
            }
        }

        [RelayCommand]
        public void OpenDataFolder()
        {
            try
            {
                if (System.IO.Directory.Exists(DataDirectory))
                {
                    Process.Start("explorer.exe", DataDirectory);
                }
            }
            catch { }
        }
    }
}
