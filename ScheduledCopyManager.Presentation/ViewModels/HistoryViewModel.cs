using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class HistoryViewModel : ObservableObject
    {
        private readonly IHistoryRepository _historyRepository;
        private readonly Services.IDialogService _dialogService;
        private readonly ILogService _logService;

        [ObservableProperty]
        private string _searchText = string.Empty;

        public ObservableCollection<HistoryEntry> HistoryEntries { get; } = new();

        public HistoryViewModel(
            IHistoryRepository historyRepository,
            Services.IDialogService dialogService,
            ILogService logService)
        {
            _historyRepository = historyRepository;
            _dialogService = dialogService;
            _logService = logService;
        }

        public async Task InitializeAsync()
        {
            await LoadHistoryAsync();
        }

        partial void OnSearchTextChanged(string value)
        {
            _ = LoadHistoryAsync();
        }

        [RelayCommand]
        public async Task LoadHistoryAsync()
        {
            try
            {
                var history = await _historyRepository.GetAllAsync();
                HistoryEntries.Clear();

                var filtered = history.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    filtered = filtered.Where(h =>
                        h.JobName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        h.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var entry in filtered)
                {
                    HistoryEntries.Add(entry);
                }
                OnPropertyChanged(nameof(HasHistoryEntries));
            }
            catch (Exception ex)
            {
                _logService.LogError("Geçmiş yüklenirken hata", ex);
            }
        }

        public bool HasHistoryEntries => HistoryEntries.Count > 0;

        [RelayCommand]
        public async Task ShowDetailsAsync(HistoryEntry? entry)
        {
            if (entry == null) return;
            await _dialogService.ShowHistoryDetailsAsync(entry);
            await LoadHistoryAsync();
        }

        [RelayCommand]
        public async Task DeleteEntryAsync(HistoryEntry? entry)
        {
            if (entry == null) return;
            await _historyRepository.DeleteAsync(entry);
            await LoadHistoryAsync();
        }

        [RelayCommand]
        public async Task ClearHistoryAsync()
        {
            bool confirm = await _dialogService.ShowConfirmationAsync(
                "Geçmişi Temizle",
                "Tüm kopyalama geçmiş kaydını silmek istediğinize emin misiniz?");

            if (confirm)
            {
                await _historyRepository.ClearAllAsync();
                await LoadHistoryAsync();
                _logService.LogInformation("Kopyalama geçmişi temizlendi.");
            }
        }
    }
}
