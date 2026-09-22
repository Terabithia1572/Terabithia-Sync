using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Scheduler
{
    public class JobScheduler : IJobScheduler
    {
        private IScheduler? _scheduler;
        private readonly IJobFactory _jobFactory;
        private readonly ILogService _logService;
        private bool _isStarted = false;

        public JobScheduler(IJobFactory jobFactory, ILogService logService)
        {
            _jobFactory = jobFactory;
            _logService = logService;
        }

        public async Task StartAsync()
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            if (_isStarted)
            {
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler.StartAsync: Already started.");
                return;
            }

            var factory = new StdSchedulerFactory();
            _scheduler = await factory.GetScheduler();
            _scheduler.JobFactory = _jobFactory;
            await _scheduler.Start();
            _isStarted = true;
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler Quartz engine started successfully.");
        }

        public async Task ShutdownAsync()
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            if (_scheduler != null && !_scheduler.IsShutdown)
            {
                await _scheduler.Shutdown(waitForJobsToComplete: true);
                _isStarted = false;
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler Quartz engine shut down.");
            }
        }

        public async Task ScheduleJobAsync(Job job)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            if (_scheduler == null) await StartAsync();

            await UnscheduleJobAsync(job.Id);

            if (!job.Enabled)
            {
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler.ScheduleJobAsync SKIPPED (Job disabled): JobId={job.Id}, Name='{job.Name}'");
                return;
            }

            var quartzJob = JobBuilder.Create<QuartzCopyJob>()
                .WithIdentity(job.Id.ToString(), "CopyJobs")
                .UsingJobData("JobId", job.Id.ToString())
                .UsingJobData("DryRun", false)
                .Build();

            ITrigger trigger = BuildTriggerForJob(job);

            await _scheduler!.ScheduleJob(quartzJob, trigger);

            var nextFire = trigger.GetNextFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Scheduled job '{job.Name}' ({job.Id}) TriggerKey='{trigger.Key}', ScheduleType={job.Schedule.ScheduleType}, NextFire={nextFire}, MisfireInstruction={trigger.MisfireInstruction}");
            await LogQuartzInventoryAsync();
        }

        public async Task LogQuartzInventoryAsync()
        {
            if (_scheduler == null) return;
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;

            try
            {
                var jobKeys = await _scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
                foreach (var jk in jobKeys)
                {
                    var triggers = await _scheduler.GetTriggersOfJob(jk);
                    foreach (var tr in triggers)
                    {
                        var state = await _scheduler.GetTriggerState(tr.Key);
                        var prevFire = tr.GetPreviousFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
                        var nextFire = tr.GetNextFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
                        var finalFire = tr.FinalFireTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
                        _logService.LogInformation($"[QUARTZ INVENTORY] [{nowMs}] [PID:{pid}] JobKey='{jk}' TriggerKey='{tr.Key}' JobId={jk.Name} TriggerState={state} PreviousFire={prevFire} NextFire={nextFire} FinalFire={finalFire} MisfireInstruction={tr.MisfireInstruction}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"[QUARTZ INVENTORY ERROR] {ex.Message}");
            }
        }

        public async Task UnscheduleJobAsync(Guid jobId)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId.ToString(), "CopyJobs");
            if (await _scheduler.CheckExists(jobKey))
            {
                await _scheduler.DeleteJob(jobKey);
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Unscheduled job {jobId}");
            }
        }

        public async Task RescheduleJobAsync(Job job)
        {
            await ScheduleJobAsync(job);
        }

        public async Task TriggerJobNowAsync(Guid jobId, bool dryRun = false, bool isRecoveryResume = false, ExecutionTriggerSource source = ExecutionTriggerSource.ManualRun, IReadOnlyList<FileItemResult>? retryFiles = null)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            if (_scheduler == null) await StartAsync();

            var dataMap = new JobDataMap
            {
                { "JobId", jobId.ToString() },
                { "DryRun", dryRun },
                { "ManualTrigger", source == ExecutionTriggerSource.ManualRun },
                { "IsRecoveryResume", isRecoveryResume || source == ExecutionTriggerSource.RecoveryResume },
                { "TriggerSource", source.ToString() }
            };

            if (retryFiles != null && retryFiles.Count > 0)
            {
                try
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(retryFiles);
                    dataMap.Put("HistoryRetryFilesJson", json);
                }
                catch { }
            }

            var jobKey = new JobKey(jobId.ToString(), "CopyJobs");
            if (await _scheduler!.CheckExists(jobKey))
            {
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler.TriggerJobNowAsync (Existing JobKey): JobId={jobId}, Source={source}, DryRun={dryRun}, RetryFilesCount={retryFiles?.Count ?? 0}");
                await _scheduler.TriggerJob(jobKey, dataMap);
            }
            else
            {
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] JobScheduler.TriggerJobNowAsync (Ad-Hoc Temp Job): JobId={jobId}, Source={source}, DryRun={dryRun}, RetryFilesCount={retryFiles?.Count ?? 0}");
                var tempJob = JobBuilder.Create<QuartzCopyJob>()
                    .WithIdentity($"Temp_{jobId}_{Guid.NewGuid()}", "TempJobs")
                    .UsingJobData(dataMap)
                    .Build();

                var tempTrigger = TriggerBuilder.Create()
                    .StartNow()
                    .Build();

                await _scheduler.ScheduleJob(tempJob, tempTrigger);
            }
        }

        public async Task<DateTime?> GetNextExecutionTimeAsync(Job job)
        {
            if (!job.Enabled) return null;
            try
            {
                ITrigger trigger = BuildTriggerForJob(job);
                var nextFire = trigger.GetNextFireTimeUtc() ?? trigger.GetFireTimeAfter(DateTimeOffset.UtcNow);
                return nextFire?.ToLocalTime().DateTime;
            }
            catch
            {
                return null;
            }
        }

        private ITrigger BuildTriggerForJob(Job job)
        {
            var triggerBuilder = TriggerBuilder.Create()
                .WithIdentity($"Trigger_{job.Id}", "CopyTriggers");

            if (job.Schedule.StartDate.HasValue)
            {
                triggerBuilder.StartAt(job.Schedule.StartDate.Value.ToUniversalTime());
            }

            if (job.Schedule.EndDate.HasValue)
            {
                triggerBuilder.EndAt(job.Schedule.EndDate.Value.ToUniversalTime());
            }

            var time = job.Schedule.TimeOfDay;

            switch (job.Schedule.ScheduleType)
            {
                case ScheduleType.OneTime:
                    var runAt = (job.Schedule.StartDate ?? DateTime.Now.Date).Date + time;
                    if (runAt < DateTime.Now) runAt = DateTime.Now.AddSeconds(5);
                    triggerBuilder.StartAt(runAt.ToUniversalTime());
                    break;

                case ScheduleType.Daily:
                    string cronDaily = $"{time.Seconds} {time.Minutes} {time.Hours} * * ?";
                    triggerBuilder.WithCronSchedule(cronDaily, x => ApplyMisfireInstruction(x, job.MissedJobBehavior));
                    break;

                case ScheduleType.Weekly:
                    var daysList = GetDaysOfWeekString(job.Schedule.DaysOfWeek);
                    string cronWeekly = $"{time.Seconds} {time.Minutes} {time.Hours} ? * {daysList}";
                    triggerBuilder.WithCronSchedule(cronWeekly, x => ApplyMisfireInstruction(x, job.MissedJobBehavior));
                    break;

                case ScheduleType.Monthly:
                    int day = Math.Clamp(job.Schedule.DayOfMonth, 1, 31);
                    string cronMonthly = $"{time.Seconds} {time.Minutes} {time.Hours} {day} * ?";
                    triggerBuilder.WithCronSchedule(cronMonthly, x => ApplyMisfireInstruction(x, job.MissedJobBehavior));
                    break;

                case ScheduleType.Cron:
                    string cronExpr = !string.IsNullOrWhiteSpace(job.Schedule.CronExpression)
                        ? job.Schedule.CronExpression
                        : "0 0 12 * * ?";
                    triggerBuilder.WithCronSchedule(cronExpr, x => ApplyMisfireInstruction(x, job.MissedJobBehavior));
                    break;
            }

            return triggerBuilder.Build();
        }

        private void ApplyMisfireInstruction(CronScheduleBuilder builder, MissedJobBehavior behavior)
        {
            switch (behavior)
            {
                case MissedJobBehavior.RunImmediately:
                    builder.WithMisfireHandlingInstructionFireAndProceed();
                    break;
                case MissedJobBehavior.Skip:
                    builder.WithMisfireHandlingInstructionDoNothing();
                    break;
                case MissedJobBehavior.Reschedule:
                    builder.WithMisfireHandlingInstructionIgnoreMisfires();
                    break;
            }
        }

        private string GetDaysOfWeekString(bool[] days)
        {
            if (days == null || days.Length < 7) return "MON-FRI";
            var dayNames = new string[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
            var activeDays = new List<string>();
            for (int i = 0; i < 7; i++)
            {
                if (days[i]) activeDays.Add(dayNames[i]);
            }
            return activeDays.Count > 0 ? string.Join(",", activeDays) : "MON-FRI";
        }
    }
}
