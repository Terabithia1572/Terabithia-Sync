using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Presentation.Helpers;
using ScheduledCopyManager.Presentation.Services;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class ActiveJobViewModel : ObservableObject
    {
        private readonly IJobExecutionManager _jobExecutionManager;
        private readonly IDialogService _dialogService;

        public Guid JobId { get; }

        [ObservableProperty] private string _jobName = string.Empty;
        [ObservableProperty] private ExecutionState _state = ExecutionState.Running;
        [ObservableProperty] private string _statusText = "Çalışıyor...";
        [ObservableProperty] private double _progressPercentage;
        [ObservableProperty] private string _bytesCopiedText = "0 B / 0 B";
        [ObservableProperty] private string _currentSpeedText = "0 B/s";
        [ObservableProperty] private string _averageSpeedText = "0 B/s";
        [ObservableProperty] private string _remainingBytesText = "0 B";
        [ObservableProperty] private string _remainingTimeText = "Hesaplanıyor...";
        [ObservableProperty] private string _elapsedTimeText = "00:00:00";
        [ObservableProperty] private string _filesSummaryText = "0/0 dosya";
        [ObservableProperty] private string _currentFileName = string.Empty;
        [ObservableProperty] private string _currentFilePath = string.Empty;
        [ObservableProperty] private string _trimmedFilePath = string.Empty;
        [ObservableProperty] private string _currentFileProgressText = string.Empty;
        [ObservableProperty] private double _currentFilePercentage;
        [ObservableProperty] private string _startTimeText = string.Empty;
        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private bool _isRunning = true;
        [ObservableProperty] private int _failedFilesCount;
        [ObservableProperty] private int _skippedFilesCount;
        [ObservableProperty] private int _copiedFilesCount;
        [ObservableProperty] private int _totalFilesCount;

        public DateTime StartTime { get; }

        public ActiveJobViewModel(
            Guid jobId,
            string jobName,
            IJobExecutionManager jobExecutionManager,
            IDialogService dialogService)
        {
            JobId = jobId;
            JobName = jobName;
            _jobExecutionManager = jobExecutionManager ?? throw new ArgumentNullException(nameof(jobExecutionManager));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            StartTime = DateTime.Now;
            StartTimeText = $"Başlangıç: {StartTime:HH:mm:ss}";
        }

        public void UpdateProgress(FileCopyProgress p)
        {
            if (p == null) return;

            // Non-blocking dispatch to UI thread so background copy thread is never stalled by UI rendering
            if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyProgressSnapshot(p));
            }
            else
            {
                ApplyProgressSnapshot(p);
            }
        }

        private void ApplyProgressSnapshot(FileCopyProgress p)
        {
            JobName = p.JobName;
            State = p.State;
            IsPaused = p.State == ExecutionState.Paused;
            IsRunning = p.State == ExecutionState.Running;

            StatusText = p.State switch
            {
                ExecutionState.Running => "Çalışıyor...",
                ExecutionState.Paused => "DURAKLATILDI",
                ExecutionState.Stopped => "DURDURULDU",
                ExecutionState.Cancelled => "İPTAL EDİLDİ",
                ExecutionState.Completed => "Tamamlandı",
                ExecutionState.Failed => "Hata",
                ExecutionState.DestinationUnavailable => "Hedef sürücü bekleniyor",
                ExecutionState.WaitingForDrive => "Hedef sürücü bekleniyor",
                _ => !string.IsNullOrEmpty(p.StatusMessage) ? p.StatusMessage : "Çalışıyor..."
            };

            ProgressPercentage = Math.Round(p.Percentage, 1);
            BytesCopiedText = $"{FormattingHelpers.FormatBytes(p.BytesCopied)} / {FormattingHelpers.FormatBytes(p.TotalBytes)}";
            
            CurrentSpeedText = (p.State == ExecutionState.Paused || p.State == ExecutionState.DestinationUnavailable || p.State == ExecutionState.WaitingForDrive)
                ? "0 B/s"
                : FormattingHelpers.FormatSpeed(p.CurrentBytesPerSecond);

            AverageSpeedText = FormattingHelpers.FormatSpeed(p.AverageBytesPerSecond);

            long remainingBytes = Math.Max(0, p.TotalBytes - p.BytesCopied);
            RemainingBytesText = FormattingHelpers.FormatBytes(remainingBytes);

            RemainingTimeText = FormattingHelpers.FormatRemainingTime(p.RemainingTime, p.State);
            ElapsedTimeText = $"{p.ElapsedTime:hh\\:mm\\:ss}";

            CopiedFilesCount = p.FilesCopied;
            SkippedFilesCount = p.FilesSkipped;
            FailedFilesCount = p.FilesFailed;
            TotalFilesCount = p.TotalFiles;

            FilesSummaryText = $"{p.FilesCopied}/{p.TotalFiles} dosya | {p.FilesSkipped} atlandı | {p.FilesFailed} hatalı";

            CurrentFileName = p.CurrentFileName;
            CurrentFilePath = p.CurrentFilePath;
            TrimmedFilePath = FormattingHelpers.TrimPath(p.CurrentFilePath);

            if (p.CurrentFileSize > 0)
            {
                CurrentFilePercentage = Math.Round(p.CurrentFilePercentage, 1);
                CurrentFileProgressText = $"{FormattingHelpers.FormatBytes(p.CurrentFileBytesCopied)} / {FormattingHelpers.FormatBytes(p.CurrentFileSize)} (%{CurrentFilePercentage:N0})";
            }
            else
            {
                CurrentFilePercentage = 0;
                CurrentFileProgressText = string.Empty;
            }

            // Diagnostic publication logging
            System.Diagnostics.Debug.WriteLine($"[PROGRESS PUBLISH] ElapsedMs={p.ElapsedTime.TotalMilliseconds:N0} DisplayedBytes={p.BytesCopied} DisplayedSpeed={p.CurrentBytesPerSecond:N0} B/s DisplayedPercent={p.Percentage:N1}%");
        }

        [RelayCommand]
        public async Task PauseAsync()
        {
            await _jobExecutionManager.PauseJobAsync(JobId);
            IsPaused = true;
            IsRunning = false;
            StatusText = "DURAKLATILDI";
            CurrentSpeedText = "Duraklatıldı";
            RemainingTimeText = "Duraklatıldı";
        }

        [RelayCommand]
        public void Resume()
        {
            _jobExecutionManager.ResumeJob(JobId);
            IsPaused = false;
            IsRunning = true;
            StatusText = "Çalışıyor...";
        }

        [RelayCommand]
        public async Task StopAsync()
        {
            await _jobExecutionManager.StopJobAsync(JobId);
            IsRunning = false;
            IsPaused = false;
            StatusText = "DURDURULDU";
        }

        [RelayCommand]
        public async Task CancelAsync()
        {
            bool confirm = await _dialogService.ShowConfirmationAsync(
                "İptal Onayı",
                "Bu kopyalama işlemini iptal etmek istediğinizden emin misiniz?");

            if (confirm)
            {
                await _jobExecutionManager.CancelJobAsync(JobId);
                IsRunning = false;
                IsPaused = false;
                StatusText = "İPTAL EDİLDİ";
            }
        }
    }
}
