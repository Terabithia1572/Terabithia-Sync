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

        private readonly IJobExecutionGate? _jobExecutionGate;
        private readonly ICheckpointRepository? _checkpointRepository;
        private readonly IJobScheduler? _jobScheduler;

        public HistoryEntry Entry { get; }
        public event Action? RequestClose;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _selectedStatusFilter = "Tümü";
        [ObservableProperty] private bool _isRetrying = false;
        [ObservableProperty] private string? _retryStatusMessage;

        public ObservableCollection<string> StatusFilters { get; } = new()
        {
            "Tümü", "Başarılı", "Başarısız", "Yarım Kalan / İptal", "Atlanan"
        };

        public ObservableCollection<FileItemResultViewModel> DisplayFileResults { get; } = new();
        public ObservableCollection<FileItemResultViewModel> FailedFileResults { get; } = new();

        public bool HasFailedFiles => Entry.FilesFailed > 0 || Entry.FilesIncomplete > 0 || FailedFileResults.Any();
        public bool HasNoFileDetails => !DisplayFileResults.Any();

        public HistoryDetailViewModel(
            HistoryEntry entry,
            IJobRepository jobRepository,
            IFileCopyService fileCopyService,
            IHistoryRepository historyRepository,
            Services.IDialogService dialogService,
            IJobExecutionGate? jobExecutionGate = null,
            ICheckpointRepository? checkpointRepository = null,
            IJobScheduler? jobScheduler = null)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _jobRepository = jobRepository;
            _fileCopyService = fileCopyService;
            _historyRepository = historyRepository;
            _dialogService = dialogService;
            _jobExecutionGate = jobExecutionGate;
            _checkpointRepository = checkpointRepository;
            _jobScheduler = jobScheduler;

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
            else if (SelectedStatusFilter == "Yarım Kalan / İptal")
            {
                query = query.Where(f => f.Status == FileItemStatus.Incomplete || f.Status == FileItemStatus.Cancelled);
            }
            else if (SelectedStatusFilter == "Atlanan")
            {
                query = query.Where(f => f.Status == FileItemStatus.Skipped);
            }

            foreach (var item in query)
            {
                var vm = new FileItemResultViewModel(item);
                DisplayFileResults.Add(vm);
                if (item.Status == FileItemStatus.Failed || item.Status == FileItemStatus.Retrying || item.Status == FileItemStatus.Incomplete || item.Status == FileItemStatus.Cancelled)
                {
                    FailedFileResults.Add(vm);
                }
            }

            OnPropertyChanged(nameof(HasFailedFiles));
            OnPropertyChanged(nameof(HasNoFileDetails));
            OnPropertyChanged(nameof(CanRetryPrimary));
            OnPropertyChanged(nameof(PrimaryRetryButtonText));
        }

        public bool CanRetryPrimary => Entry.FilesFailed > 0 || Entry.FilesIncomplete > 0 || FailedFileResults.Any(f => f.Item.Status == FileItemStatus.Failed || f.Item.Status == FileItemStatus.Incomplete || f.Item.Status == FileItemStatus.Cancelled || f.Item.Status == FileItemStatus.Retrying);

        public string PrimaryRetryButtonText
        {
            get
            {
                bool hasFailed = Entry.FilesFailed > 0 || FailedFileResults.Any(f => f.Item.Status == FileItemStatus.Failed || f.Item.Status == FileItemStatus.Retrying);
                bool hasIncomplete = Entry.FilesIncomplete > 0 || FailedFileResults.Any(f => f.Item.Status == FileItemStatus.Incomplete || f.Item.Status == FileItemStatus.Cancelled);

                if (hasFailed && !hasIncomplete) return "⚡ Başarısızları Yeniden Dene";
                if (!hasFailed && hasIncomplete) return "⚡ Yarım Kalanları Yeniden Dene";
                if (hasFailed && hasIncomplete) return "⚡ Tamamlanmayanları Yeniden Dene";
                return "⚡ Yeniden Dene";
            }
        }

        [RelayCommand]
        public async Task RetryAllFailedAsync()
        {
            if (_jobExecutionGate != null)
            {
                var gateRes = await _jobExecutionGate.CanExecuteAsync(Entry.JobId, ExecutionTriggerSource.HistoryRetry);
                if (!gateRes.Allowed)
                {
                    await _dialogService.ShowMessageAsync("İşlem Engellendi", gateRes.Reason);
                    return;
                }
            }

            var failedItems = _allFileResults.Where(f => f.Status == FileItemStatus.Failed || f.Status == FileItemStatus.Retrying || f.Status == FileItemStatus.Incomplete || f.Status == FileItemStatus.Cancelled).ToList();
            if (!failedItems.Any())
            {
                await _dialogService.ShowMessageAsync("Bilgi", "Yeniden denenecek başarısız veya yarım kalmış dosya bulunmuyor.");
                return;
            }

            IsRetrying = true;
            RetryStatusMessage = $"{failedItems.Count} dosya için yeniden deneme başlatılıyor...";

            try
            {
                if (_jobScheduler != null)
                {
                    await _jobScheduler.TriggerJobNowAsync(Entry.JobId, dryRun: false, isRecoveryResume: false, source: ExecutionTriggerSource.HistoryRetry, retryFiles: failedItems);
                    RetryStatusMessage = "Yeniden deneme kopyalama görevlerine gönderildi. Aktif Görevler sayfasından canlı takip edebilirsiniz.";
                    await _dialogService.ShowMessageAsync("Yeniden Deneme Başlatıldı", "Yeniden deneme görevi başlatıldı. İlerlemeyi 'Kopyalama Görevleri' ekranındaki Aktif Görevler kartından canlı olarak takip edebilirsiniz.");
                }
                else
                {
                    var job = await _jobRepository.GetByIdAsync(Entry.JobId) ?? new Job { Id = Entry.JobId, Name = Entry.JobName, DestinationPath = failedItems.FirstOrDefault()?.DestinationPath ?? string.Empty };
                    var retryResult = await _fileCopyService.RetryFailedFilesAsync(job, failedItems, false, null, CancellationToken.None);
                    Entry.FilesCopied += retryResult.FilesCopied;
                    Entry.FilesFailed = Math.Max(0, Entry.FilesFailed - retryResult.FilesCopied);
                    Entry.FilesIncomplete = Math.Max(0, Entry.FilesIncomplete - retryResult.FilesCopied);
                    Entry.BytesCopied += retryResult.BytesCopied;
                    Entry.Status = (Entry.FilesFailed == 0 && Entry.FilesIncomplete == 0) ? JobResultStatus.Success : JobResultStatus.PartialSuccess;
                    await _historyRepository.UpdateAsync(Entry);
                    FilterResults();
                    RetryStatusMessage = $"Yeniden deneme tamamlandı: {retryResult.FilesCopied} dosya aktarıldı.";
                    await _dialogService.ShowMessageAsync("İşlem Tamamlandı", RetryStatusMessage);
                }
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

            if (_jobExecutionGate != null)
            {
                var gateRes = await _jobExecutionGate.CanExecuteAsync(Entry.JobId, ExecutionTriggerSource.HistoryRetrySelected);
                if (!gateRes.Allowed)
                {
                    await _dialogService.ShowMessageAsync("İşlem Engellendi", gateRes.Reason);
                    return;
                }
            }

            IsRetrying = true;
            RetryStatusMessage = $"'{fileVm.FileName}' yeniden deneniyor...";

            try
            {
                if (_jobScheduler != null)
                {
                    await _jobScheduler.TriggerJobNowAsync(Entry.JobId, dryRun: false, isRecoveryResume: false, source: ExecutionTriggerSource.HistoryRetrySelected, retryFiles: new List<FileItemResult> { fileVm.Item });
                    RetryStatusMessage = $"'{fileVm.FileName}' için yeniden deneme başlatıldı.";
                    await _dialogService.ShowMessageAsync("Yeniden Deneme Başlatıldı", $"'{fileVm.FileName}' için yeniden deneme kopyalama görevi başlatıldı.");
                }
                else
                {
                    var job = await _jobRepository.GetByIdAsync(Entry.JobId) ?? new Job { Id = Entry.JobId, Name = Entry.JobName, DestinationPath = fileVm.Item.DestinationPath };
                    var retryResult = await _fileCopyService.RetryFailedFilesAsync(job, new List<FileItemResult> { fileVm.Item }, false, null, CancellationToken.None);

                    if (retryResult.Success || fileVm.Item.Status == FileItemStatus.Completed)
                    {
                        Entry.FilesCopied++;
                        if (fileVm.Item.Status == FileItemStatus.Failed) Entry.FilesFailed = Math.Max(0, Entry.FilesFailed - 1);
                        if (fileVm.Item.Status == FileItemStatus.Incomplete) Entry.FilesIncomplete = Math.Max(0, Entry.FilesIncomplete - 1);
                        Entry.BytesCopied += fileVm.Item.FileSize;
                        if (Entry.FilesFailed == 0 && Entry.FilesIncomplete == 0) Entry.Status = JobResultStatus.Success;

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
        public long BytesTransferred => Item.BytesTransferred > 0 ? Item.BytesTransferred : (Item.Status == FileItemStatus.Completed || Item.Status == FileItemStatus.Skipped ? Item.FileSize : 0);
        public string ErrorMessage => Item.ErrorMessage ?? string.Empty;
        public int RetryCount => Item.RetryCount;
        public DateTime Timestamp => Item.Timestamp;
        public bool CanRetrySingle => Item.Status == FileItemStatus.Failed || Item.Status == FileItemStatus.Incomplete || Item.Status == FileItemStatus.Cancelled || Item.Status == FileItemStatus.Retrying;

        public string Icon => Item.Status switch
        {
            FileItemStatus.Completed => "✓",
            FileItemStatus.Skipped => "⏩",
            FileItemStatus.Failed => "✕",
            FileItemStatus.Incomplete => "⚠️",
            FileItemStatus.Cancelled => "🛑",
            FileItemStatus.Retrying => "🔄",
            FileItemStatus.Pending => "⏳",
            _ => "ℹ️"
        };

        public string IconColor => Item.Status switch
        {
            FileItemStatus.Completed => "#10B981",
            FileItemStatus.Skipped => "#F59E0B",
            FileItemStatus.Failed => "#EF4444",
            FileItemStatus.Incomplete => "#F59E0B",
            FileItemStatus.Cancelled => "#EF4444",
            FileItemStatus.Retrying => "#3B82F6",
            _ => "#6B7280"
        };

        public string StatusText => Item.Status switch
        {
            FileItemStatus.Completed => "Kopyalandı",
            FileItemStatus.Skipped => "Atlandı",
            FileItemStatus.Failed => "Başarısız",
            FileItemStatus.Incomplete => "Yarım Kaldı",
            FileItemStatus.Cancelled => "İptal Edildi",
            FileItemStatus.Retrying => "Yeniden Deneniyor",
            FileItemStatus.Pending => "Bekliyor",
            FileItemStatus.Copying => "Kopyalanıyor",
            FileItemStatus.WaitingForDestination => "Hedef Bekleniyor",
            _ => Item.Status.ToString()
        };

        public FileItemResultViewModel(FileItemResult item)
        {
            Item = item;
        }
    }
}
