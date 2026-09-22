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
    public class CopyBufferSizeOption
    {
        public string DisplayName { get; set; } = string.Empty;
        public int SizeBytes { get; set; }
    }

    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly IStartupService _startupService;
        private readonly Services.IDialogService _dialogService;
        private readonly ILogService _logService;
        private readonly ILocalizationService? _localizationService;

        private Settings _currentSettings = new();

        [ObservableProperty] private bool _startWithWindows;
        [ObservableProperty] private bool _startMinimized;
        [ObservableProperty] private bool _enableTrayIcon;
        [ObservableProperty] private bool _closeToTray;
        [ObservableProperty] private bool _enableNotifications;
        [ObservableProperty] private bool _notifyOnSuccess;
        [ObservableProperty] private bool _notifyOnFailure;
        [ObservableProperty] private bool _notifyOnDestinationUnavailable;
        [ObservableProperty] private bool _notifyOnRecoveryWaiting;
        [ObservableProperty] private int _defaultRetryCount;
        [ObservableProperty] private int _defaultRetryDelaySeconds;
        [ObservableProperty] private ConflictPolicy _defaultConflictPolicy;
        [ObservableProperty] private int _maxConcurrentJobs;
        [ObservableProperty] private string _logDirectory = string.Empty;
        [ObservableProperty] private string _dataDirectory = string.Empty;
        [ObservableProperty] private string _selectedLanguage = "tr-TR";
        [ObservableProperty] private CopyBufferSizeOption? _selectedCopyBufferOption;

        public Array ConflictPolicies => Enum.GetValues(typeof(ConflictPolicy));
        public string[] AvailableLanguages => new[] { "tr-TR", "en-US" };

        public CopyBufferSizeOption[] CopyBufferOptions => new[]
        {
            new CopyBufferSizeOption { DisplayName = "512 KiB (524.288 Bayt) [Üretim Varsayılanı]", SizeBytes = 524288 },
            new CopyBufferSizeOption { DisplayName = "1 MiB (1.048.576 Bayt)", SizeBytes = 1048576 },
            new CopyBufferSizeOption { DisplayName = "2 MiB (2.097.152 Bayt)", SizeBytes = 2097152 },
            new CopyBufferSizeOption { DisplayName = "4 MiB (4.194.304 Bayt)", SizeBytes = 4194304 }
        };

        public SettingsViewModel(
            ISettingsRepository settingsRepository,
            IStartupService startupService,
            Services.IDialogService dialogService,
            ILogService logService,
            ILocalizationService? localizationService = null)
        {
            _settingsRepository = settingsRepository;
            _startupService = startupService;
            _dialogService = dialogService;
            _logService = logService;
            _localizationService = localizationService;
        }

        public async Task InitializeAsync()
        {
            _currentSettings = await _settingsRepository.GetAsync();

            StartWithWindows = _currentSettings.StartWithWindows;
            StartMinimized = _currentSettings.StartMinimized;
            EnableTrayIcon = _currentSettings.EnableTrayIcon;
            CloseToTray = _currentSettings.CloseToTray;
            EnableNotifications = _currentSettings.EnableNotifications;
            NotifyOnSuccess = _currentSettings.NotifyOnSuccess;
            NotifyOnFailure = _currentSettings.NotifyOnFailure;
            NotifyOnDestinationUnavailable = _currentSettings.NotifyOnDestinationUnavailable;
            NotifyOnRecoveryWaiting = _currentSettings.NotifyOnRecoveryWaiting;
            DefaultRetryCount = _currentSettings.DefaultRetryCount;
            DefaultRetryDelaySeconds = _currentSettings.DefaultRetryDelaySeconds;
            DefaultConflictPolicy = _currentSettings.DefaultConflictPolicy;
            MaxConcurrentJobs = _currentSettings.MaxConcurrentJobs;
            LogDirectory = _currentSettings.LogDirectory;
            DataDirectory = _currentSettings.DataDirectory;
            SelectedLanguage = string.IsNullOrWhiteSpace(_currentSettings.Language) ? "tr-TR" : _currentSettings.Language;

            int validatedSize = _currentSettings.GetValidatedCopyBufferSize();
            SelectedCopyBufferOption = Array.Find(CopyBufferOptions, o => o.SizeBytes == validatedSize) ?? CopyBufferOptions[0];
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
                _currentSettings.NotifyOnSuccess = NotifyOnSuccess;
                _currentSettings.NotifyOnFailure = NotifyOnFailure;
                _currentSettings.NotifyOnDestinationUnavailable = NotifyOnDestinationUnavailable;
                _currentSettings.NotifyOnRecoveryWaiting = NotifyOnRecoveryWaiting;
                _currentSettings.DefaultRetryCount = Math.Max(1, DefaultRetryCount);
                _currentSettings.DefaultRetryDelaySeconds = Math.Max(1, DefaultRetryDelaySeconds);
                _currentSettings.DefaultConflictPolicy = DefaultConflictPolicy;
                _currentSettings.MaxConcurrentJobs = Math.Max(1, MaxConcurrentJobs);
                _currentSettings.Language = SelectedLanguage;
                _currentSettings.CopyBufferSize = SelectedCopyBufferOption?.SizeBytes ?? 524288;

                await _settingsRepository.SaveAsync(_currentSettings);
                _startupService.SetStartup(StartWithWindows);
                _localizationService?.SetLanguage(SelectedLanguage);

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
