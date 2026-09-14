using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IJobRepository _jobRepository;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IJobScheduler _jobScheduler;
        private readonly ILogService _logService;

        public DashboardViewModel DashboardVM { get; }
        public JobsViewModel JobsVM { get; }
        public HistoryViewModel HistoryVM { get; }
        public LogsViewModel LogsVM { get; }
        public SettingsViewModel SettingsVM { get; }
        public AboutViewModel AboutVM { get; }
        public WelcomeViewModel WelcomeVM { get; }

        [ObservableProperty]
        private object _currentViewModel;

        [ObservableProperty]
        private string _activeViewTitle = "Özet Panosu";

        [ObservableProperty]
        private bool _isWelcomeVisible = false;

        public event Action? RequestShowWindow;
        public event Action? RequestCloseApp;

        public MainViewModel(
            DashboardViewModel dashboardVM,
            JobsViewModel jobsVM,
            HistoryViewModel historyVM,
            LogsViewModel logsVM,
            SettingsViewModel settingsVM,
            AboutViewModel aboutVM,
            WelcomeViewModel welcomeVM,
            IJobRepository jobRepository,
            ISettingsRepository settingsRepository,
            IJobScheduler jobScheduler,
            ILogService logService)
        {
            DashboardVM = dashboardVM;
            JobsVM = jobsVM;
            HistoryVM = historyVM;
            LogsVM = logsVM;
            SettingsVM = settingsVM;
            AboutVM = aboutVM;
            WelcomeVM = welcomeVM;

            _jobRepository = jobRepository;
            _settingsRepository = settingsRepository;
            _jobScheduler = jobScheduler;
            _logService = logService;

            _currentViewModel = DashboardVM;

            WelcomeVM.RequestClose += () => IsWelcomeVisible = false;
        }

        public async Task InitializeAsync()
        {
            try
            {
                // Start scheduler
                await _jobScheduler.StartAsync();

                // Load jobs into scheduler
                var jobs = await _jobRepository.GetAllAsync();
                foreach (var j in jobs)
                {
                    if (j.Enabled)
                    {
                        await _jobScheduler.ScheduleJobAsync(j);
                    }
                }

                var settings = await _settingsRepository.GetAsync();
                if (settings.IsFirstRun)
                {
                    IsWelcomeVisible = true;
                }

                await DashboardVM.InitializeAsync();
                await JobsVM.InitializeAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError("MainViewModel başlatılırken hata", ex);
            }
        }

        [RelayCommand]
        public async Task NavigateToDashboardAsync()
        {
            CurrentViewModel = DashboardVM;
            ActiveViewTitle = "Özet Panosu";
            await DashboardVM.RefreshDataAsync();
        }

        [RelayCommand]
        public async Task NavigateToJobsAsync()
        {
            CurrentViewModel = JobsVM;
            ActiveViewTitle = "Kopyalama Görevleri";
            await JobsVM.LoadJobsAsync();
        }

        [RelayCommand]
        public async Task NavigateToHistoryAsync()
        {
            CurrentViewModel = HistoryVM;
            ActiveViewTitle = "Kopyalama Geçmişi";
            await HistoryVM.LoadHistoryAsync();
        }

        [RelayCommand]
        public void NavigateToLogs()
        {
            CurrentViewModel = LogsVM;
            ActiveViewTitle = "Uygulama Günlükleri";
            LogsVM.Initialize();
        }

        [RelayCommand]
        public async Task NavigateToSettingsAsync()
        {
            CurrentViewModel = SettingsVM;
            ActiveViewTitle = "Ayarlar";
            await SettingsVM.InitializeAsync();
        }

        [RelayCommand]
        public void NavigateToAbout()
        {
            CurrentViewModel = AboutVM;
            ActiveViewTitle = "Hakkında";
        }

        // Tray Menu Commands
        [RelayCommand]
        public void OpenApp()
        {
            RequestShowWindow?.Invoke();
        }

        [RelayCommand]
        public async Task RunAllJobsNowAsync()
        {
            var jobs = await _jobRepository.GetAllAsync();
            int count = 0;
            foreach (var j in jobs.Where(x => x.Enabled))
            {
                await _jobScheduler.TriggerJobNowAsync(j.Id, dryRun: false);
                count++;
            }

            _logService.LogInformation($"Tüm görevler başlatıldı ({count} görev)");
            System.Windows.MessageBox.Show($"{count} aktif kopyalama görevi başlatıldı.", "Terabithia Sync", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        [RelayCommand]
        public async Task ExitAppAsync()
        {
            await _jobScheduler.ShutdownAsync();
            RequestCloseApp?.Invoke();
        }
    }
}
