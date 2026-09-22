using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly IJobRepository _jobRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IJobScheduler _jobScheduler;
        private readonly ILogService _logService;
        private readonly Services.IDialogService? _dialogService;

        [ObservableProperty] private int _totalJobs;
        [ObservableProperty] private int _enabledJobs;
        [ObservableProperty] private int _todayRunsCount;
        [ObservableProperty] private int _successCount;
        [ObservableProperty] private int _failureCount;
        [ObservableProperty] private string _nextScheduledRunText = "Planlanmış görev yok";
        [ObservableProperty] private string _lastExecutionText = "Henüz çalıştırılmadı";
        [ObservableProperty] private string _systemStatusText = "Zamanlayıcı Aktif ve Çalışıyor";
        [ObservableProperty] private HistoryEntry? _selectedHistoryEntry;

        public ObservableCollection<HistoryEntry> RecentHistory { get; } = new();

        public DashboardViewModel(
            IJobRepository jobRepository,
            IHistoryRepository historyRepository,
            IJobScheduler jobScheduler,
            ILogService logService,
            Services.IDialogService? dialogService = null)
        {
            _jobRepository = jobRepository;
            _historyRepository = historyRepository;
            _jobScheduler = jobScheduler;
            _logService = logService;
            _dialogService = dialogService;
        }

        public async Task InitializeAsync()
        {
            await RefreshDataAsync();
        }

        [RelayCommand]
        public async Task RefreshDataAsync()
        {
            try
            {
                var jobs = await _jobRepository.GetAllAsync();
                TotalJobs = jobs.Count;
                EnabledJobs = jobs.Count(j => j.Enabled);

                // Find next execution
                DateTime? earliestNext = null;
                foreach (var job in jobs.Where(j => j.Enabled))
                {
                    var next = await _jobScheduler.GetNextExecutionTimeAsync(job);
                    if (next.HasValue && (!earliestNext.HasValue || next.Value < earliestNext.Value))
                    {
                        earliestNext = next;
                    }
                }

                NextScheduledRunText = earliestNext.HasValue
                    ? earliestNext.Value.ToString("dd.MM.yyyy HH:mm:ss")
                    : "Planlanmış görev yok";

                // Last execution
                var lastJobRun = jobs.Where(j => j.LastRun.HasValue).OrderByDescending(j => j.LastRun!.Value).FirstOrDefault();
                LastExecutionText = lastJobRun?.LastRun.HasValue == true
                    ? $"{lastJobRun.Name} ({lastJobRun.LastRun.Value:dd.MM.yyyy HH:mm:ss})"
                    : "Henüz çalıştırılmadı";

                // History
                var history = await _historyRepository.GetAllAsync();
                DateTime today = DateTime.Today;
                TodayRunsCount = history.Count(h => h.StartTime.Date == today);
                SuccessCount = history.Count(h => h.Status == JobResultStatus.Success);
                FailureCount = history.Count(h => h.Status == JobResultStatus.Failure || h.Status == JobResultStatus.PartialSuccess);

                RecentHistory.Clear();
                foreach (var item in history.Take(10))
                {
                    RecentHistory.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("Özet panosu verileri güncellenirken hata oluştu", ex);
            }
        }

        [RelayCommand]
        public async Task ShowHistoryDetailsAsync(HistoryEntry? entry)
        {
            var target = entry ?? SelectedHistoryEntry;
            if (target == null || _dialogService == null) return;
            await _dialogService.ShowHistoryDetailsAsync(target);
            await RefreshDataAsync();
        }
    }
}
