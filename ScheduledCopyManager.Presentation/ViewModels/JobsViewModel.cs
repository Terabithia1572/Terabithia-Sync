using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private JobItemViewModel? _selectedJob;

        public ObservableCollection<JobItemViewModel> Jobs { get; } = new();

        public JobsViewModel(
            IJobRepository jobRepository,
            IJobScheduler jobScheduler,
            IDialogService dialogService,
            ILogService logService)
        {
            _jobRepository = jobRepository;
            _jobScheduler = jobScheduler;
            _dialogService = dialogService;
            _logService = logService;
        }

        public async Task InitializeAsync()
        {
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
                    var nextRun = await _jobScheduler.GetNextExecutionTimeAsync(j);
                    j.NextRun = nextRun;
                    Jobs.Add(new JobItemViewModel(j, this));
                }
                FilterJobs();
            }
            catch (Exception ex)
            {
                _logService.LogError("Görevler yüklenirken hata oluştu", ex);
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

            await _jobScheduler.TriggerJobNowAsync(target.Id, dryRun: false);
            await _dialogService.ShowMessageAsync("Görev Başlatıldı", $"'{target.Name}' görevi arka planda başlatıldı.");
        }

        [RelayCommand]
        public async Task DryRunAsync(object? param)
        {
            var target = ExtractJob(param) ?? SelectedJob?.Job;
            if (target == null) return;

            await _jobScheduler.TriggerJobNowAsync(target.Id, dryRun: true);
            await _dialogService.ShowMessageAsync("Ön İzleme Başlatıldı", $"'{target.Name}' görevi ön izleme modunda başlatıldı.");
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

        public JobItemViewModel(Job job, JobsViewModel parent)
        {
            Job = job;
            _parent = parent;
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
