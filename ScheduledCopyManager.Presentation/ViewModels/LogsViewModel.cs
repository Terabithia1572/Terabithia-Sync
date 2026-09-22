using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        private readonly ILogService _logService;
        private readonly IHistoryRepository _historyRepository;

        [ObservableProperty] private string _selectedLevelFilter = "HEPSİ";
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private int _selectedTabIndex = 0;
        [ObservableProperty] private LogEntry? _selectedLogEntry;

        private readonly Services.IDialogService? _dialogService;

        public ObservableCollection<LogEntry> LogEntries { get; } = new();
        public ObservableCollection<GroupedLogItemViewModel> GroupedLogs { get; } = new();

        public string[] LevelFilters { get; } = new string[] { "HEPSİ", "BİLGİ", "UYARI", "HATA" };

        public LogsViewModel(ILogService logService, IHistoryRepository historyRepository, Services.IDialogService? dialogService = null)
        {
            _logService = logService;
            _historyRepository = historyRepository;
            _dialogService = dialogService;
        }

        public async void Initialize()
        {
            await RefreshLogsAsync();
        }

        partial void OnSelectedLevelFilterChanged(string value) => _ = RefreshLogsAsync();
        partial void OnSearchTextChanged(string value) => _ = RefreshLogsAsync();

        [RelayCommand]
        public async Task RefreshLogsAsync()
        {
            try
            {
                // Refresh System Logs
                var logs = _logService.GetRecentLogs(300);
                LogEntries.Clear();

                var filteredSystemLogs = logs.AsEnumerable();
                if (SelectedLevelFilter != "HEPSİ")
                {
                    filteredSystemLogs = filteredSystemLogs.Where(l => l.Level.Equals(SelectedLevelFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    filteredSystemLogs = filteredSystemLogs.Where(l => l.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                                                   (l.Exception != null && l.Exception.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
                }

                foreach (var log in filteredSystemLogs)
                {
                    LogEntries.Add(log);
                }

                // Refresh Grouped Job Logs
                var history = await _historyRepository.GetAllAsync();
                GroupedLogs.Clear();

                var historyQuery = history.OrderByDescending(h => h.StartTime).AsEnumerable();
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    historyQuery = historyQuery.Where(h =>
                        h.JobName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        (h.FileResults != null && h.FileResults.Any(f => f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))));
                }

                foreach (var entry in historyQuery)
                {
                    int totalFilesCount = entry.TotalFilesPlanned > 0
                        ? entry.TotalFilesPlanned
                        : (entry.FileResults != null && entry.FileResults.Any()
                            ? entry.FileResults.Count
                            : entry.FilesCopied + entry.FilesSkipped + entry.FilesFailed + entry.FilesIncomplete);

                    var groupVm = new GroupedLogItemViewModel
                    {
                        JobName = entry.JobName,
                        Timestamp = entry.StartTime,
                        Status = entry.Status,
                        TotalFiles = totalFilesCount,
                        FilesCopied = entry.FilesCopied,
                        FilesSkipped = entry.FilesSkipped,
                        FilesFailed = entry.FilesFailed,
                        FilesIncomplete = entry.FilesIncomplete,
                        BytesCopied = entry.BytesCopied
                    };

                    if (entry.FileResults != null)
                    {
                        foreach (var f in entry.FileResults)
                        {
                            groupVm.ChildFileLogs.Add(new FileItemResultViewModel(f));
                        }
                    }

                    GroupedLogs.Add(groupVm);
                }

                OnPropertyChanged(nameof(HasGroupedLogs));
                OnPropertyChanged(nameof(HasLogEntries));
            }
            catch { }
        }

        public bool HasGroupedLogs => GroupedLogs.Count > 0;
        public bool HasLogEntries => LogEntries.Count > 0;

        [RelayCommand]
        public async Task ClearLogsAsync()
        {
            if (_dialogService != null)
            {
                bool confirm = await _dialogService.ShowConfirmationAsync(
                    "Günlükleri Temizle",
                    "Sistem günlüklerini temizlemek istediğinizden emin misiniz?");
                if (!confirm) return;
            }

            _logService.ClearLogs();
            await RefreshLogsAsync();
        }

        [RelayCommand]
        public void OpenLogFolder()
        {
            try
            {
                string dir = _logService.GetLogDirectory();
                if (System.IO.Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", dir);
                }
            }
            catch { }
        }

        [RelayCommand]
        public async Task ShowLogDetailsAsync(LogEntry? entry)
        {
            var target = entry ?? SelectedLogEntry;
            if (target == null || _dialogService == null) return;
            await _dialogService.ShowLogDetailsAsync(target);
        }
    }
}
