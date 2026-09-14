using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class HistoryDetailViewModel : ObservableObject
    {
        private readonly IJobRepository _jobRepository;
        private readonly IFileCopyService _fileCopyService;
        private readonly IHistoryRepository _historyRepository;
        private readonly Services.IDialogService _dialogService;
        private readonly List<FileItemResult> _allFileResults = new();

        public HistoryEntry Entry { get; }
        public event Action? RequestClose;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _selectedStatusFilter = "Tümü";
        [ObservableProperty] private bool _isRetrying = false;
        [ObservableProperty] private string? _retryStatusMessage;

        public ObservableCollection<string> StatusFilters { get; } = new()
        {
            "Tümü", "Başarılı", "Başarısız", "Atlanan"
        };

        public ObservableCollection<FileItemResultViewModel> DisplayFileResults { get; } = new();
        public ObservableCollection<FileItemResultViewModel> FailedFileResults { get; } = new();

        public bool HasFailedFiles => Entry.FilesFailed > 0 || FailedFileResults.Any();

        public HistoryDetailViewModel(
            HistoryEntry entry,
            IJobRepository jobRepository,
            IFileCopyService fileCopyService,
            IHistoryRepository historyRepository,
            Services.IDialogService dialogService)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _jobRepository = jobRepository;
            _fileCopyService = fileCopyService;
            _historyRepository = historyRepository;
            _dialogService = dialogService;

            if (entry.FileResults != null)
            {
                _allFileResults.AddRange(entry.FileResults);
            }

            FilterResults();
        }

        partial void OnSearchTextChanged(string value) => FilterResults();
        partial void OnSelectedStatusFilterChanged(string value) => FilterResults();

        private void FilterResults()
        {
            DisplayFileResults.Clear();
            FailedFileResults.Clear();

            var query = _allFileResults.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(f =>
                    f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    (f.ErrorMessage != null && f.ErrorMessage.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
            }

            if (SelectedStatusFilter == "Başarılı")
            {
                query = query.Where(f => f.Status == FileItemStatus.Completed);
            }
            else if (SelectedStatusFilter == "Başarısız")
            {
                query = query.Where(f => f.Status == FileItemStatus.Failed || f.Status == FileItemStatus.Retrying);
            }
            else if (SelectedStatusFilter == "Atlanan")
            {
                query = query.Where(f => f.Status == FileItemStatus.Skipped);
            }

            foreach (var item in query)
            {
                var vm = new FileItemResultViewModel(item);
                DisplayFileResults.Add(vm);
                if (item.Status == FileItemStatus.Failed || item.Status == FileItemStatus.Retrying)
                {
                    FailedFileResults.Add(vm);
                }
            }

            OnPropertyChanged(nameof(HasFailedFiles));
        }

        [RelayCommand]
        public async Task RetryAllFailedAsync()
        {
            var failedItems = _allFileResults.Where(f => f.Status == FileItemStatus.Failed || f.Status == FileItemStatus.Retrying).ToList();
            if (!failedItems.Any())
            {
                await _dialogService.ShowMessageAsync("Bilgi", "Yeniden denenecek başarısız dosya bulunmuyor.");
                return;
            }

            var job = await _jobRepository.GetByIdAsync(Entry.JobId);
            if (job == null)
            {
                job = new Job
                {
                    Id = Entry.JobId,
                    Name = Entry.JobName,
                    DestinationPath = failedItems.FirstOrDefault()?.DestinationPath ?? string.Empty
                };
            }

            IsRetrying = true;
            RetryStatusMessage = $"{failedItems.Count} başarısız dosya yeniden deneniyor...";

            try
            {
                var retryResult = await _fileCopyService.RetryFailedFilesAsync(job, failedItems, false, null, CancellationToken.None);

                Entry.FilesCopied += retryResult.FilesCopied;
                Entry.FilesFailed = Math.Max(0, Entry.FilesFailed - retryResult.FilesCopied);
                Entry.BytesCopied += retryResult.BytesCopied;
                Entry.Status = Entry.FilesFailed == 0 ? JobResultStatus.Success : JobResultStatus.PartialSuccess;

                await _historyRepository.UpdateAsync(Entry);
                FilterResults();

                RetryStatusMessage = $"Yeniden deneme tamamlandı: {retryResult.FilesCopied} dosya başarıyla aktarıldı.";
                await _dialogService.ShowMessageAsync("İşlem Tamamlandı", RetryStatusMessage);
            }
            catch (Exception ex)
            {
                RetryStatusMessage = $"Yeniden deneme sırasında hata: {ex.Message}";
            }
            finally
            {
                IsRetrying = false;
            }
        }

        [RelayCommand]
        public async Task RetrySingleFileAsync(FileItemResultViewModel? fileVm)
        {
            if (fileVm == null || fileVm.Item == null) return;

            var job = await _jobRepository.GetByIdAsync(Entry.JobId);
            if (job == null)
            {
                job = new Job
                {
                    Id = Entry.JobId,
                    Name = Entry.JobName,
                    DestinationPath = fileVm.Item.DestinationPath
                };
            }

            IsRetrying = true;
            RetryStatusMessage = $"'{fileVm.FileName}' yeniden deneniyor...";

            try
            {
                var retryResult = await _fileCopyService.RetryFailedFilesAsync(job, new List<FileItemResult> { fileVm.Item }, false, null, CancellationToken.None);

                if (retryResult.Success || fileVm.Item.Status == FileItemStatus.Completed)
                {
                    Entry.FilesCopied++;
                    Entry.FilesFailed = Math.Max(0, Entry.FilesFailed - 1);
                    Entry.BytesCopied += fileVm.Item.FileSize;
                    if (Entry.FilesFailed == 0) Entry.Status = JobResultStatus.Success;

                    await _historyRepository.UpdateAsync(Entry);
                    FilterResults();

                    await _dialogService.ShowMessageAsync("Başarılı", $"'{fileVm.FileName}' başarıyla kopyalandı.");
                }
                else
                {
                    FilterResults();
                    await _dialogService.ShowMessageAsync("Hata", $"'{fileVm.FileName}' kopyalanamadı: {fileVm.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                RetryStatusMessage = $"Hata: {ex.Message}";
            }
            finally
            {
                IsRetrying = false;
            }
        }

        [RelayCommand]
        public void Close()
        {
            RequestClose?.Invoke();
        }
    }

    public partial class FileItemResultViewModel : ObservableObject
    {
        public FileItemResult Item { get; }
        public string FileName => Item.FileName;
        public string RelativePath => Item.RelativePath;
        public long FileSize => Item.FileSize;
        public string ErrorMessage => Item.ErrorMessage ?? string.Empty;
        public int RetryCount => Item.RetryCount;
        public DateTime Timestamp => Item.Timestamp;

        public string Icon => Item.Status switch
        {
            FileItemStatus.Completed => "✓",
            FileItemStatus.Skipped => "⏩",
            FileItemStatus.Failed => "✕",
            FileItemStatus.Retrying => "🔄",
            FileItemStatus.Pending => "⏳",
            _ => "ℹ️"
        };

        public string IconColor => Item.Status switch
        {
            FileItemStatus.Completed => "#10B981",
            FileItemStatus.Skipped => "#F59E0B",
            FileItemStatus.Failed => "#EF4444",
            FileItemStatus.Retrying => "#3B82F6",
            _ => "#6B7280"
        };

        public string StatusText => Item.Status switch
        {
            FileItemStatus.Completed => "Kopyalandı",
            FileItemStatus.Skipped => "Atlandı",
            FileItemStatus.Failed => "Başarısız",
            FileItemStatus.Retrying => "Yeniden Deneniyor",
            FileItemStatus.Pending => "Bekliyor",
            FileItemStatus.Copying => "Kopyalanıyor",
            FileItemStatus.WaitingForDestination => "Hedef Bekleniyor",
            FileItemStatus.Cancelled => "İptal Edildi",
            _ => Item.Status.ToString()
        };

        public FileItemResultViewModel(FileItemResult item)
        {
            Item = item;
        }
    }
}
