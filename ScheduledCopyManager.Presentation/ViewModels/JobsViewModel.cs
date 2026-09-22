using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Presentation.Services;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class JobsViewModel : ObservableObject
    {
        private readonly IJobRepository _jobRepository;
        private readonly IJobScheduler _jobScheduler;
        private readonly IDialogService _dialogService;
        private readonly ILogService _logService;
        private readonly IJobExecutionManager? _jobExecutionManager;
        private readonly ICheckpointRepository? _checkpointRepository;
        private readonly IJobExecutionGate? _jobExecutionGate;
        private readonly IPreflightValidationService? _preflightValidationService;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private JobItemViewModel? _selectedJob;

        public ObservableCollection<JobItemViewModel> Jobs { get; } = new();
        public ObservableCollection<ActiveJobViewModel> ActiveJobs { get; } = new();
        public ObservableCollection<JobCheckpoint> RecoverableCheckpoints { get; } = new();

        public bool HasActiveJobs => ActiveJobs.Count > 0;
        public bool HasRecoverableJobs => RecoverableCheckpoints.Count > 0;

        public JobsViewModel(
            IJobRepository jobRepository,
            IJobScheduler jobScheduler,
            IDialogService dialogService,
            ILogService logService,
            IJobExecutionManager? jobExecutionManager = null,
            ICheckpointRepository? checkpointRepository = null,
            IJobExecutionGate? jobExecutionGate = null,
            IPreflightValidationService? preflightValidationService = null)
        {
            _jobRepository = jobRepository;
            _jobScheduler = jobScheduler;
            _dialogService = dialogService;
            _logService = logService;
            _jobExecutionManager = jobExecutionManager;
            _checkpointRepository = checkpointRepository;
            _jobExecutionGate = jobExecutionGate;
            _preflightValidationService = preflightValidationService;

            if (_jobExecutionManager != null)
            {
                _jobExecutionManager.ProgressUpdated += OnProgressUpdated;
                _jobExecutionManager.JobCompleted += OnJobCompleted;
            }

            ActiveJobs.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasActiveJobs));
            RecoverableCheckpoints.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasRecoverableJobs));
        }

        public async Task InitializeAsync()
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobsViewModel.InitializeAsync INVOKED");
            await LoadJobsAsync();
        }

        partial void OnSearchTextChanged(string value)
        {
            FilterJobs();
        }

        [RelayCommand]
        public async Task LoadJobsAsync()
        {
            try
            {
                var allJobs = await _jobRepository.GetAllAsync();
                Jobs.Clear();
                foreach (var j in allJobs)
                {
                    var nextRun = _jobScheduler != null ? await _jobScheduler.GetNextExecutionTimeAsync(j) : null;
                    j.NextRun = nextRun;
                    Jobs.Add(new JobItemViewModel(j, this));
                }
                FilterJobs();

                if (_checkpointRepository != null)
                {
                    var recoverables = await _checkpointRepository.GetRecoverableCheckpointsAsync();
                    var uniqueRecoverables = recoverables
                        .GroupBy(cp => cp.JobId)
                        .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
                        .ToList();

                    RecoverableCheckpoints.Clear();
                    foreach (var cp in uniqueRecoverables)
                    {
                        if (_jobExecutionManager != null && _jobExecutionManager.IsJobActive(cp.JobId))
                        {
                            cp.InterruptionReason = "Yeniden deneme çalışıyor...";
                        }
                        RecoverableCheckpoints.Add(cp);
                    }
                    OnPropertyChanged(nameof(HasRecoverableJobs));
                }
                OnPropertyChanged(nameof(HasJobs));
            }
            catch (Exception ex)
            {
                _logService.LogError("Görevler yüklenirken hata oluştu", ex);
            }
        }

        public bool HasJobs => Jobs.Count > 0;

        [RelayCommand]
        public async Task ResumeRecoveryAsync(object? param)
        {
            if (param is JobCheckpoint cp)
            {
                string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                int pid = Environment.ProcessId;
                int tid = Environment.CurrentManagedThreadId;
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobsViewModel.ResumeRecoveryAsync INVOKED for JobId={cp.JobId}, CheckpointState={cp.CurrentState}");

                if (_jobExecutionGate != null)
                {
                    var gateResult = await _jobExecutionGate.CanExecuteAsync(cp.JobId, ExecutionTriggerSource.RecoveryResume);
                    if (!gateResult.Allowed)
                    {
                        await _dialogService.ShowMessageAsync(
                            "Çakışan İşlem",
                            "Bu görev için başka bir kopyalama işlemi zaten çalışıyor.");
                        return;
                    }
                }

                await _jobScheduler.TriggerJobNowAsync(cp.JobId, dryRun: false, isRecoveryResume: true);
                await LoadJobsAsync();
            }
        }

        [RelayCommand]
        public async Task ShowRecoveryDetailsAsync(object? param)
        {
            if (param is JobCheckpoint cp)
            {
                await _dialogService.ShowRecoveryDetailsAsync(cp);
            }
        }

        [RelayCommand]
        public async Task DiscardRecoveryAsync(object? param)
        {
            if (param is JobCheckpoint cp)
            {
                bool confirm = await _dialogService.ShowConfirmationAsync(
                    "Kurtarma İptal Onayı",
                    "Bu göreve ait kurtarma kaydı silinecek. Daha sonra kaldığı yerden devam edilemeyecek. Devam etmek istiyor musunuz?");

                if (confirm && _checkpointRepository != null)
                {
                    await _checkpointRepository.DeleteCheckpointAsync(cp.JobId);
                    RecoverableCheckpoints.Remove(cp);
                    OnPropertyChanged(nameof(HasRecoverableJobs));
                    _logService.LogInformation($"[EXECUTION TRACE] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Kullanıcı kurtarma kaydını sildi. JobId={cp.JobId}");
                    await LoadJobsAsync();
                }
            }
        }

        private void FilterJobs()
        {
            foreach (var item in Jobs)
            {
                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    item.IsVisible = true;
                }
                else
                {
                    item.IsVisible = item.Job.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                     item.Job.DestinationPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [RelayCommand]
        public async Task CreateJobAsync()
        {
            var createdJob = await _dialogService.ShowJobEditorAsync(null);
            if (createdJob != null)
            {
                await _jobRepository.AddAsync(createdJob);
                await _jobScheduler.ScheduleJobAsync(createdJob);
                await LoadJobsAsync();
                _logService.LogInformation($"Yeni görev oluşturuldu: '{createdJob.Name}'");
            }
        }

        [RelayCommand]
        public async Task EditJobAsync(object? param)
        {
            var target = ExtractJob(param) ?? SelectedJob?.Job;
            if (target == null) return;

            var updatedJob = await _dialogService.ShowJobEditorAsync(target);
            if (updatedJob != null)
            {
                await _jobRepository.UpdateAsync(updatedJob);
                await _jobScheduler.RescheduleJobAsync(updatedJob);
                await LoadJobsAsync();
                _logService.LogInformation($"Görev güncellendi: '{updatedJob.Name}'");
            }
        }

        [RelayCommand]
        public async Task DeleteJobAsync(object? param)
        {
            var target = ExtractJob(param) ?? SelectedJob?.Job;
            if (target == null) return;

            bool confirm = await _dialogService.ShowConfirmationAsync(
                "Görevi Sil",
                $"'{target.Name}' adlı görevi silmek istediğinize emin misiniz?");

            if (confirm)
            {
                await _jobScheduler.UnscheduleJobAsync(target.Id);
                await _jobRepository.DeleteAsync(target.Id);
                await LoadJobsAsync();
                _logService.LogInformation($"Görev silindi: '{target.Name}'");
            }
        }

        [RelayCommand]
        public async Task ToggleEnabledAsync(object? param)
        {
            var job = ExtractJob(param) ?? SelectedJob?.Job;
            if (job == null) return;

            job.Enabled = !job.Enabled;
            await _jobRepository.UpdateAsync(job);
            if (job.Enabled)
                await _jobScheduler.ScheduleJobAsync(job);
            else
                await _jobScheduler.UnscheduleJobAsync(job.Id);

            await LoadJobsAsync();
        }

        [RelayCommand]
        public async Task RunNowAsync(object? param)
        {
            var target = ExtractJob(param) ?? SelectedJob?.Job;
            if (target == null) return;

            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobsViewModel.RunNowAsync INVOKED for JobId={target.Id}, JobName='{target.Name}'");

            if (_jobExecutionGate != null)
            {
                var gateResult = await _jobExecutionGate.CanExecuteAsync(target.Id, ExecutionTriggerSource.ManualRun);
                if (!gateResult.Allowed)
                {
                    await _dialogService.ShowMessageAsync(
                        "Tamamlanmamış Kopyalama Bulundu",
                        "Bu görev için tamamlanmamış veya durdurulmuş bir kopyalama bulunmaktadır. Önce mevcut kopyalamaya devam edin veya kurtarma kaydını iptal edin.");
                    return;
                }
            }
            else if (_checkpointRepository != null)
            {
                var cp = await _checkpointRepository.GetCheckpointAsync(target.Id);
                if (cp != null && cp.IsRecoverable &&
                    (cp.CurrentState == ExecutionState.Stopped ||
                     cp.CurrentState == ExecutionState.Paused ||
                     cp.CurrentState == ExecutionState.Running ||
                     cp.CurrentState == ExecutionState.Failed ||
                     cp.CurrentState == ExecutionState.Cancelled ||
                     cp.DestinationWasUnavailable) &&
                    (cp.PendingFiles > 0 || cp.FailedFiles > 0 || cp.CompletedFiles < cp.TotalFiles))
                {
                    await _dialogService.ShowMessageAsync(
                        "Tamamlanmamış Kopyalama Bulundu",
                        "Bu görev için tamamlanmamış bir kopyalama bulunmaktadır. Önce mevcut kopyalamaya devam edin veya kurtarma kaydını iptal edin.");
                    return;
                }
            }

            if (_jobExecutionManager != null && _jobExecutionManager.IsJobRunning(target.Id))
            {
                await _dialogService.ShowMessageAsync("Görev Zaten Çalışıyor", $"'{target.Name}' görevi şu anda zaten çalışıyor.");
                return;
            }

            if (_preflightValidationService != null)
            {
                var preflightResult = await _preflightValidationService.ValidateJobAsync(target);
                if (preflightResult.HasBlockingErrors)
                {
                    var blockingIssue = preflightResult.Issues.First(i => i.Severity == PreflightSeverity.BlockingError);
                    await _dialogService.ShowMessageAsync(blockingIssue.Title, $"{blockingIssue.Message}\n\nÖneri: {blockingIssue.SuggestedAction ?? "Lütfen ayarlarınızı kontrol edin."}");
                    return;
                }
                if (preflightResult.HasWarnings)
                {
                    string warningsStr = string.Join("\n• ", preflightResult.Issues.Where(i => i.Severity == PreflightSeverity.Warning).Select(i => i.Message));
                    bool continueAnyway = await _dialogService.ShowConfirmationAsync(
                        "Ön Kontrol Uyarısı",
                        $"Görevinizde aşağıdaki uyarılar tespit edildi:\n\n• {warningsStr}\n\nYine de çalıştırmak istiyor musunuz?");

                    if (!continueAnyway) return;
                }
            }

            await _jobScheduler.TriggerJobNowAsync(target.Id, dryRun: false);
            NotifyJobStatusChanged(target.Id);
            await _dialogService.ShowMessageAsync("Görev Başlatıldı", $"'{target.Name}' görevi arka planda başlatıldı.");
        }

        [RelayCommand]
        public async Task DryRunAsync(object? param)
        {
            var target = ExtractJob(param) ?? SelectedJob?.Job;
            if (target == null) return;

            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobsViewModel.DryRunAsync INVOKED for JobId={target.Id}, JobName='{target.Name}'");

            if (_jobExecutionGate != null)
            {
                var gateResult = await _jobExecutionGate.CanExecuteAsync(target.Id, ExecutionTriggerSource.ManualRun);
                if (!gateResult.Allowed)
                {
                    await _dialogService.ShowMessageAsync(
                        "Tamamlanmamış Kopyalama Bulundu",
                        "Bu görev için tamamlanmamış veya durdurulmuş bir kopyalama bulunmaktadır. Önce mevcut kopyalamaya devam edin veya kurtarma kaydını iptal edin.");
                    return;
                }
            }
            else if (_checkpointRepository != null)
            {
                var cp = await _checkpointRepository.GetCheckpointAsync(target.Id);
                if (cp != null && cp.IsRecoverable &&
                    (cp.CurrentState == ExecutionState.Stopped ||
                     cp.CurrentState == ExecutionState.Paused ||
                     cp.CurrentState == ExecutionState.Running ||
                     cp.CurrentState == ExecutionState.Failed ||
                     cp.CurrentState == ExecutionState.Cancelled ||
                     cp.DestinationWasUnavailable) &&
                    (cp.PendingFiles > 0 || cp.FailedFiles > 0 || cp.CompletedFiles < cp.TotalFiles))
                {
                    await _dialogService.ShowMessageAsync(
                        "Tamamlanmamış Kopyalama Bulundu",
                        "Bu görev için tamamlanmamış bir kopyalama bulunmaktadır. Önce mevcut kopyalamaya devam edin veya kurtarma kaydını iptal edin.");
                    return;
                }
            }

            if (_jobExecutionManager != null && _jobExecutionManager.IsJobRunning(target.Id))
            {
                await _dialogService.ShowMessageAsync("Görev Zaten Çalışıyor", $"'{target.Name}' görevi şu anda zaten çalışıyor.");
                return;
            }

            if (_preflightValidationService != null)
            {
                var preflightResult = await _preflightValidationService.ValidateJobAsync(target);
                if (preflightResult.HasBlockingErrors)
                {
                    var blockingIssue = preflightResult.Issues.First(i => i.Severity == PreflightSeverity.BlockingError);
                    await _dialogService.ShowMessageAsync(blockingIssue.Title, $"{blockingIssue.Message}\n\nÖneri: {blockingIssue.SuggestedAction ?? "Lütfen ayarlarınızı kontrol edin."}");
                    return;
                }
                if (preflightResult.HasWarnings)
                {
                    string warningsStr = string.Join("\n• ", preflightResult.Issues.Where(i => i.Severity == PreflightSeverity.Warning).Select(i => i.Message));
                    bool continueAnyway = await _dialogService.ShowConfirmationAsync(
                        "Ön Kontrol Uyarısı",
                        $"Görevinizde aşağıdaki uyarılar tespit edildi:\n\n• {warningsStr}\n\nYine de ön izleme yapmak istiyor musunuz?");

                    if (!continueAnyway) return;
                }
            }

            await _jobScheduler.TriggerJobNowAsync(target.Id, dryRun: true);
            NotifyJobStatusChanged(target.Id);
            await _dialogService.ShowMessageAsync("Ön İzleme Başlatıldı", $"'{target.Name}' görevi ön izleme modunda başlatıldı.");
        }

        public bool IsJobActive(Guid jobId)
        {
            return _jobExecutionManager?.IsJobActive(jobId) ?? false;
        }

        public bool IsJobRunning(Guid jobId)
        {
            return _jobExecutionManager?.IsJobRunning(jobId) ?? false;
        }

        public bool IsJobPaused(Guid jobId)
        {
            return _jobExecutionManager?.IsJobPaused(jobId) ?? false;
        }

        private readonly ConcurrentDictionary<Guid, FileCopyProgress> _latestProgressSnapshots = new();
        private int _isProgressDispatchPending = 0;

        private void OnProgressUpdated(FileCopyProgress progress)
        {
            if (progress != null && progress.JobId != Guid.Empty)
            {
                _latestProgressSnapshots[progress.JobId] = progress;
            }

            if (Interlocked.CompareExchange(ref _isProgressDispatchPending, 1, 0) != 0)
            {
                return;
            }

            ExecuteOnUIAsync(() =>
            {
                try
                {
                    var keys = _latestProgressSnapshots.Keys.ToList();
                    foreach (var jobId in keys)
                    {
                        if (_latestProgressSnapshots.TryRemove(jobId, out var snapshot))
                        {
                            if (snapshot.JobId == Guid.Empty) continue;

                            var existing = ActiveJobs.FirstOrDefault(x => x.JobId == snapshot.JobId);
                            if (existing == null)
                            {
                                if (snapshot.State == ExecutionState.Completed ||
                                    snapshot.State == ExecutionState.Failed ||
                                    snapshot.State == ExecutionState.Cancelled ||
                                    snapshot.State == ExecutionState.Stopped)
                                {
                                    continue;
                                }

                                existing = new ActiveJobViewModel(snapshot.JobId, snapshot.JobName, _jobExecutionManager!, _dialogService);
                                ActiveJobs.Add(existing);
                                _logService.LogInformation($"[ACTIVE CARD TRACE] ProcessInstanceId={Environment.ProcessId} JobId={snapshot.JobId} Name='{snapshot.JobName}' Action=CREATE Reason=ProgressUpdate State={snapshot.State} ActiveCardCount={ActiveJobs.Count}");
                            }

                            existing.UpdateProgress(snapshot);
                            NotifyJobStatusChanged(snapshot.JobId);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isProgressDispatchPending, 0);

                    if (!_latestProgressSnapshots.IsEmpty)
                    {
                        OnProgressUpdated(null!);
                    }
                }
            });
        }

        private void OnJobCompleted(Guid jobId)
        {
            if (jobId == Guid.Empty) return;

            ExecuteOnUIAsync(() =>
            {
                var match = ActiveJobs.FirstOrDefault(x => x.JobId == jobId);
                if (match != null)
                {
                    _logService.LogInformation($"[ACTIVE CARD TRACE] ProcessInstanceId={Environment.ProcessId} JobId={jobId} Action=REMOVE_SCHEDULED Reason=JobCompleted");
                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        ExecuteOnUIAsync(() =>
                        {
                            if (ActiveJobs.Contains(match))
                            {
                                ActiveJobs.Remove(match);
                                _logService.LogInformation($"[ACTIVE CARD TRACE] ProcessInstanceId={Environment.ProcessId} JobId={jobId} Action=REMOVE Reason=JobCompleted ActiveCardCount={ActiveJobs.Count}");
                            }
                        });
                    });
                }
                NotifyJobStatusChanged(jobId);
            });
        }

        private void NotifyJobStatusChanged(Guid jobId)
        {
            var item = Jobs.FirstOrDefault(x => x.Job.Id == jobId);
            item?.NotifyStatusChanged();
        }

        private void ExecuteOnUI(Action action)
        {
            ExecuteOnUIAsync(action);
        }

        private void ExecuteOnUIAsync(Action action)
        {
            if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        private Job? ExtractJob(object? param)
        {
            if (param is Job j) return j;
            if (param is JobItemViewModel vm) return vm.Job;
            return null;
        }
    }

    public partial class JobItemViewModel : ObservableObject
    {
        public Job Job { get; }
        private readonly JobsViewModel _parent;

        [ObservableProperty]
        private bool _isVisible = true;

        public bool IsActive => _parent.IsJobActive(Job.Id);
        public bool IsRunning => _parent.IsJobRunning(Job.Id);
        public bool IsPaused => _parent.IsJobPaused(Job.Id);
        public bool CanRun => !IsActive;

        public string RunButtonText => IsRunning
            ? "⚡ Çalışıyor..."
            : (IsPaused ? "⏸️ Duraklatıldı" : "▶ Çalıştır");

        public string CurrentStateText
        {
            get
            {
                if (IsRunning) return "ÇALIŞIYOR";
                if (IsPaused) return "DURAKLATILDI";
                return Job.LastResult switch
                {
                    JobResultStatus.Success => "BAŞARILI",
                    JobResultStatus.Failure => "BAŞARISIZ",
                    JobResultStatus.PartialSuccess => "KISMİ BAŞARILI",
                    JobResultStatus.Skipped => "ATLANDI",
                    JobResultStatus.Cancelled => "İPTAL EDİLDİ",
                    _ => "BİLİNMİYOR"
                };
            }
        }

        public string CurrentStateColor
        {
            get
            {
                if (IsRunning) return "#2563EB"; // Blue
                if (IsPaused) return "#D97706"; // Amber / Orange
                return Job.LastResult switch
                {
                    JobResultStatus.Success => "#059669", // Green
                    JobResultStatus.Failure => "#DC2626", // Red
                    JobResultStatus.PartialSuccess => "#D97706", // Amber
                    JobResultStatus.Skipped => "#4B5563", // Gray
                    JobResultStatus.Cancelled => "#9333EA", // Purple
                    _ => "#64748B" // Slate Gray
                };
            }
        }

        public JobItemViewModel(Job job, JobsViewModel parent)
        {
            Job = job;
            _parent = parent;
        }

        public void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunButtonText));
            OnPropertyChanged(nameof(CurrentStateText));
            OnPropertyChanged(nameof(CurrentStateColor));
        }

        public string ScheduleSummary => Job.Schedule.ScheduleType switch
        {
            ScheduleType.Daily => $"Her gün {Job.Schedule.TimeOfDay:hh\\:mm}",
            ScheduleType.Weekly => $"Haftalık {Job.Schedule.TimeOfDay:hh\\:mm}",
            ScheduleType.Monthly => $"Her ayın {Job.Schedule.DayOfMonth}. günü {Job.Schedule.TimeOfDay:hh\\:mm}",
            ScheduleType.OneTime => $"Tek Seferlik ({Job.Schedule.StartDate:dd.MM.yyyy HH:mm})",
            ScheduleType.Cron => $"Özel Cron ({Job.Schedule.CronExpression})",
            _ => "Belirtilmedi"
        };
    }
}
