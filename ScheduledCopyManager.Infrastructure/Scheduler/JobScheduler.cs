using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly IJobRepository? _jobRepository;
        private readonly IHistoryRepository? _historyRepository;
        private bool _isStarted = false;

        public JobScheduler(
            IJobFactory jobFactory,
            ILogService logService,
            IJobRepository? jobRepository = null,
            IHistoryRepository? historyRepository = null)
        {
            _jobFactory = jobFactory;
            _logService = logService;
            _jobRepository = jobRepository;
            _historyRepository = historyRepository;
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

            DateTime now = DateTime.Now;
            var (prevOccurrence, computedNextRun) = DetermineScheduleOccurrences(job, now);
            DateTime? lastExecution = await GetLastExecutionTimeAsync(job);

            string decision = "WAIT_FOR_FUTURE_FIRE";
            bool shouldTriggerMissedImmediately = false;

            if (computedNextRun.HasValue && computedNextRun.Value > now)
            {
                if (prevOccurrence.HasValue && prevOccurrence.Value <= now)
                {
                    bool alreadyExecuted = lastExecution.HasValue && (lastExecution.Value >= prevOccurrence.Value - TimeSpan.FromMinutes(2));
                    if (alreadyExecuted)
                    {
                        decision = "NO_MISFIRE_ALREADY_COMPLETED";
                    }
                    else
                    {
                        decision = "APPLY_MISSED_JOB_BEHAVIOR";
                        if (job.MissedJobBehavior == MissedJobBehavior.RunImmediately)
                        {
                            shouldTriggerMissedImmediately = true;
                        }
                    }
                }
                else
                {
                    decision = "WAIT_FOR_FUTURE_FIRE";
                }
            }

            var quartzJob = JobBuilder.Create<QuartzCopyJob>()
                .WithIdentity(job.Id.ToString(), "CopyJobs")
                .UsingJobData("JobId", job.Id.ToString())
                .UsingJobData("DryRun", false)
                .Build();

            ITrigger trigger = BuildTriggerForJob(job);

            await _scheduler!.ScheduleJob(quartzJob, trigger);

            var quartzNextFire = trigger.GetNextFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? (computedNextRun.HasValue ? computedNextRun.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A");

            _logService.LogInformation($"[SCHEDULER STARTUP AUDIT] JobId={job.Id} JobName='{job.Name}' Now='{now:yyyy-MM-dd HH:mm:ss.fff}' ScheduleType={job.Schedule.ScheduleType} ConfiguredTime={job.Schedule.TimeOfDay} StoredNextRun='{(job.NextRun.HasValue ? job.NextRun.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A")}' ComputedNextRun='{(computedNextRun.HasValue ? computedNextRun.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A")}' PreviousScheduledOccurrence='{(prevOccurrence.HasValue ? prevOccurrence.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A")}' LastSuccessfulExecution='{(lastExecution.HasValue ? lastExecution.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A")}' TriggerStartAt='{now:yyyy-MM-dd HH:mm:ss.fff}' QuartzNextFireTime='{quartzNextFire}' MisfirePolicy={job.MissedJobBehavior} Decision={decision}");

            await LogQuartzInventoryAsync();

            if (shouldTriggerMissedImmediately)
            {
                _logService.LogInformation($"[SCHEDULER MISFIRE EXECUTION] Genuine missed occurrence detected for '{job.Name}' ({job.Id}). Triggering immediate catch-up run.");
                await TriggerJobNowAsync(job.Id, dryRun: false, isRecoveryResume: false, source: ExecutionTriggerSource.QuartzMisfire);
            }
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

        private (DateTime? prevOccurrence, DateTime? nextOccurrence) DetermineScheduleOccurrences(Job job, DateTime now)
        {
            var time = job.Schedule.TimeOfDay;
            switch (job.Schedule.ScheduleType)
            {
                case ScheduleType.OneTime:
                    var runAt = (job.Schedule.StartDate ?? now.Date).Date + time;
                    if (runAt > now) return (null, runAt);
                    else return (runAt, null);

                case ScheduleType.Daily:
                    var todaySlot = now.Date + time;
                    if (now >= todaySlot)
                        return (todaySlot, todaySlot.AddDays(1));
                    else
                        return (null, todaySlot);

                case ScheduleType.Weekly:
                    var daysList = GetDaysOfWeekString(job.Schedule.DaysOfWeek);
                    string cronWeekly = $"{time.Seconds} {time.Minutes} {time.Hours} ? * {daysList}";
                    var cronW = new CronExpression(cronWeekly);
                    var nextWUtc = cronW.GetTimeAfter(new DateTimeOffset(now.ToUniversalTime()));
                    DateTime? nextW = nextWUtc?.ToLocalTime().DateTime;

                    int dayOfWeekIdx = (int)now.DayOfWeek; // 0=Sunday..6=Saturday
                    bool isTodayActive = job.Schedule.DaysOfWeek != null && job.Schedule.DaysOfWeek.Length > dayOfWeekIdx && job.Schedule.DaysOfWeek[dayOfWeekIdx];
                    var todayWSlot = now.Date + time;

                    DateTime? prevW = null;
                    if (isTodayActive && now >= todayWSlot)
                    {
                        prevW = todayWSlot;
                    }

                    return (prevW, nextW);

                case ScheduleType.Monthly:
                    int day = Math.Clamp(job.Schedule.DayOfMonth, 1, 31);
                    string cronMonthly = $"{time.Seconds} {time.Minutes} {time.Hours} {day} * ?";
                    var cronM = new CronExpression(cronMonthly);
                    var nextMUtc = cronM.GetTimeAfter(new DateTimeOffset(now.ToUniversalTime()));
                    DateTime? nextM = nextMUtc?.ToLocalTime().DateTime;

                    bool isTodayMonthlyDay = now.Day == day;
                    var todayMSlot = now.Date + time;

                    DateTime? prevM = null;
                    if (isTodayMonthlyDay && now >= todayMSlot)
                    {
                        prevM = todayMSlot;
                    }

                    return (prevM, nextM);

                case ScheduleType.Cron:
                    string cronExpr = !string.IsNullOrWhiteSpace(job.Schedule.CronExpression) ? job.Schedule.CronExpression : "0 0 12 * * ?";
                    var cronC = new CronExpression(cronExpr);
                    var nextCUtc = cronC.GetTimeAfter(new DateTimeOffset(now.ToUniversalTime()));
                    DateTime? nextC = nextCUtc?.ToLocalTime().DateTime;

                    DateTime checkC = now.AddDays(-1);
                    DateTime? prevC = null;
                    var tC = cronC.GetTimeAfter(new DateTimeOffset(checkC.ToUniversalTime()));
                    while (tC.HasValue && tC.Value.ToLocalTime().DateTime <= now)
                    {
                        prevC = tC.Value.ToLocalTime().DateTime;
                        tC = cronC.GetTimeAfter(tC.Value);
                    }

                    if (prevC.HasValue && prevC.Value.Date != now.Date && nextC.HasValue && nextC.Value.Date == now.Date)
                    {
                        prevC = null;
                    }

                    return (prevC, nextC);

                default:
                    return (null, null);
            }
        }

        private async Task<DateTime?> GetLastExecutionTimeAsync(Job job)
        {
            DateTime? last = job.LastRun;

            if (_jobRepository != null && job.LastRun == null)
            {
                try
                {
                    var stored = await _jobRepository.GetByIdAsync(job.Id);
                    if (stored?.LastRun != null) last = stored.LastRun;
                }
                catch { }
            }

            if (_historyRepository != null)
            {
                try
                {
                    var allHistory = await _historyRepository.GetAllAsync();
                    var history = allHistory?.Where(h => h.JobId == job.Id).ToList();
                    if (history != null && history.Any())
                    {
                        var successful = history.Where(h => h.Status == JobResultStatus.Success || h.Status == JobResultStatus.PartialSuccess || h.FilesCopied > 0 || h.FilesSkipped > 0);
                        if (successful.Any())
                        {
                            var maxHistoryTime = successful.Max(h => h.StartTime);
                            if (!last.HasValue || maxHistoryTime > last.Value)
                            {
                                last = maxHistoryTime;
                            }
                        }
                    }
                }
                catch { }
            }

            return last;
        }

        private ITrigger BuildTriggerForJob(Job job)
        {
            var triggerBuilder = TriggerBuilder.Create()
                .WithIdentity($"Trigger_{job.Id}", "CopyTriggers")
                .StartAt(DateTimeOffset.UtcNow);

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
                    triggerBuilder.WithCronSchedule(cronDaily, x => x.WithMisfireHandlingInstructionDoNothing());
                    break;

                case ScheduleType.Weekly:
                    var daysList = GetDaysOfWeekString(job.Schedule.DaysOfWeek);
                    string cronWeekly = $"{time.Seconds} {time.Minutes} {time.Hours} ? * {daysList}";
                    triggerBuilder.WithCronSchedule(cronWeekly, x => x.WithMisfireHandlingInstructionDoNothing());
                    break;

                case ScheduleType.Monthly:
                    int day = Math.Clamp(job.Schedule.DayOfMonth, 1, 31);
                    string cronMonthly = $"{time.Seconds} {time.Minutes} {time.Hours} {day} * ?";
                    triggerBuilder.WithCronSchedule(cronMonthly, x => x.WithMisfireHandlingInstructionDoNothing());
                    break;

                case ScheduleType.Cron:
                    string cronExpr = !string.IsNullOrWhiteSpace(job.Schedule.CronExpression)
                        ? job.Schedule.CronExpression
                        : "0 0 12 * * ?";
                    triggerBuilder.WithCronSchedule(cronExpr, x => x.WithMisfireHandlingInstructionDoNothing());
                    break;
            }

            return triggerBuilder.Build();
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
