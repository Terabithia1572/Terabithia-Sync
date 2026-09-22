using System;
using System.IO;
using System.Threading.Tasks;
using Quartz;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;

namespace ScheduledCopyManager.Infrastructure.Scheduler
{
    [DisallowConcurrentExecution]
    public class QuartzCopyJob : IJob
    {
        private readonly IJobRepository _jobRepository;
        private readonly IFileCopyService _fileCopyService;
        private readonly IHistoryRepository _historyRepository;
        private readonly INotificationService _notificationService;
        private readonly IUsbDriveService _usbDriveService;
        private readonly ILogService _logService;
        private readonly IJobExecutionManager? _jobExecutionManager;
        private readonly ICheckpointRepository? _checkpointRepository;
        private readonly IJobExecutionGate? _jobExecutionGate;
        private readonly IPreflightValidationService? _preflightValidationService;

        public QuartzCopyJob(
            IJobRepository jobRepository,
            IFileCopyService fileCopyService,
            IHistoryRepository historyRepository,
            INotificationService notificationService,
            IUsbDriveService usbDriveService,
            ILogService logService,
            IJobExecutionManager? jobExecutionManager = null,
            ICheckpointRepository? checkpointRepository = null,
            IJobExecutionGate? jobExecutionGate = null,
            IPreflightValidationService? preflightValidationService = null)
        {
            _jobRepository = jobRepository;
            _fileCopyService = fileCopyService;
            _historyRepository = historyRepository;
            _notificationService = notificationService;
            _usbDriveService = usbDriveService;
            _logService = logService;
            _jobExecutionManager = jobExecutionManager;
            _checkpointRepository = checkpointRepository;
            _jobExecutionGate = jobExecutionGate;
            _preflightValidationService = preflightValidationService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            var dataMap = context.MergedJobDataMap;
            string? jobIdStr = dataMap.GetString("JobId");
            bool dryRun = dataMap.ContainsKey("DryRun") && dataMap.GetBoolean("DryRun");
            bool isRecoveryResume = dataMap.ContainsKey("IsRecoveryResume") && dataMap.GetBoolean("IsRecoveryResume");
            bool isManualTrigger = dataMap.ContainsKey("ManualTrigger") && dataMap.GetBoolean("ManualTrigger");

            if (!Guid.TryParse(jobIdStr, out Guid jobId))
            {
                _logService.LogError($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] QuartzCopyJob geçersiz GörevKimliği ile çağrıldı: '{jobIdStr}'");
                return;
            }

            var trigger = context.Trigger;
            string fireInstanceId = context.FireInstanceId ?? "Unknown";
            string scheduledFireTime = trigger.GetNextFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
            string previousFireTime = trigger.GetPreviousFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
            string nextFireTime = trigger.GetNextFireTimeUtc()?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "N/A";
            int refireCount = context.RefireCount;

            string? triggerSourceStr = dataMap.ContainsKey("TriggerSource") ? dataMap.GetString("TriggerSource") : null;
            ExecutionTriggerSource source;
            if (!string.IsNullOrEmpty(triggerSourceStr) && Enum.TryParse<ExecutionTriggerSource>(triggerSourceStr, out var parsedSource))
            {
                source = parsedSource;
            }
            else if (isRecoveryResume)
                source = ExecutionTriggerSource.RecoveryResume;
            else if (isManualTrigger)
                source = ExecutionTriggerSource.ManualRun;
            else if (context.RefireCount > 0)
                source = ExecutionTriggerSource.Retry;
            else
                source = ExecutionTriggerSource.QuartzScheduled;

            string? retryJson = dataMap.ContainsKey("HistoryRetryFilesJson") ? dataMap.GetString("HistoryRetryFilesJson") : null;
            System.Collections.Generic.List<FileItemResult>? retryFiles = null;
            if (!string.IsNullOrEmpty(retryJson))
            {
                try
                {
                    retryFiles = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<FileItemResult>>(retryJson);
                }
                catch { }
            }

            var cpState = ExecutionState.Idle;
            if (_checkpointRepository != null)
            {
                var cp = await _checkpointRepository.GetCheckpointAsync(jobId);
                if (cp != null) cpState = cp.CurrentState;
            }

            _logService.LogInformation($"[EXECUTION REQUEST] JobId={jobId} TriggerSource={source} CheckpointState={cpState}");
            _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] QuartzCopyJob.Execute CALLED: JobId={jobId}, Source={source}, IsRecoveryResume={isRecoveryResume}, ManualTrigger={isManualTrigger}, CheckpointState={cpState}, FireInstanceId={fireInstanceId}, PrevFire={previousFireTime}, NextFire={nextFireTime}, RefireCount={refireCount}");

            if (source == ExecutionTriggerSource.HistoryRetry || source == ExecutionTriggerSource.HistoryRetrySelected)
            {
                _logService.LogInformation($"[HISTORY RETRY REQUEST] JobId={jobId} TriggerSource={source} SelectedFilesCount={retryFiles?.Count ?? 0} ExistingCheckpointState={cpState}");
            }

            var job = await _jobRepository.GetByIdAsync(jobId);
            if (job == null)
            {
                _logService.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Zamanlanmış görev veritabanında bulunamadı: {jobId}");
                return;
            }

            if (!job.Enabled && !isManualTrigger && !isRecoveryResume && source != ExecutionTriggerSource.HistoryRetry && source != ExecutionTriggerSource.HistoryRetrySelected)
            {
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Devre dışı bırakılan '{job.Name}' görevi atlandı.");
                return;
            }

            // Central Execution Gate Check
            if (_jobExecutionGate != null)
            {
                var gateResult = await _jobExecutionGate.CanExecuteAsync(jobId, source);
                if (!gateResult.Allowed)
                {
                    _logService.LogWarning($"[EXECUTION OWNERSHIP] JobId={jobId} Owner={source} Decision=Blocked Reason='{gateResult.Reason}'");
                    return;
                }
            }
            _logService.LogInformation($"[EXECUTION OWNERSHIP] JobId={jobId} Owner={source} Decision=Allowed Reason='Gate approved'");

            // Fallback Recovery Guard if Gate was not injected
            if (_jobExecutionGate == null && !isRecoveryResume && source != ExecutionTriggerSource.HistoryRetry && source != ExecutionTriggerSource.HistoryRetrySelected && _checkpointRepository != null)
            {
                var cp = await _checkpointRepository.GetCheckpointAsync(job.Id);
                if (cp != null && cp.IsRecoverable &&
                    (cp.CurrentState == ExecutionState.Stopped ||
                     cp.CurrentState == ExecutionState.Paused ||
                     cp.CurrentState == ExecutionState.Running ||
                     cp.CurrentState == ExecutionState.Failed ||
                     cp.CurrentState == ExecutionState.Cancelled ||
                     cp.DestinationWasUnavailable) &&
                    (cp.PendingFiles > 0 || cp.FailedFiles > 0 || cp.CompletedFiles < cp.TotalFiles))
                {
                    _logService.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Kurtarılabilir görev bulundu; otomatik çalıştırma engellendi. '{job.Name}' ({job.Id}) için tamamlanmamış kurtarma kaydı (State: {cp.CurrentState}).");
                    return;
                }
            }

            // Check USB drive connection if job specifies USB destination or target volume serial
            if (job.IsUsbDestination || !string.IsNullOrEmpty(job.TargetVolumeSerialNumber))
            {
                if (!_usbDriveService.IsDriveConnected(job.DestinationPath, job.TargetVolumeSerialNumber))
                {
                    _logService.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] '{job.Name}' kopyalama görevi çalıştırılamadı. Hedef USB sürücüsü ('{job.DestinationPath}') bağlı değil veya seri numarası eşleşmiyor. Hedef sürücü bekleniyor.");

                    var usbHistoryEntry = new HistoryEntry
                    {
                        JobId = job.Id,
                        JobName = job.Name,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = JobResultStatus.Skipped,
                        Message = "Hedef sürücü bekleniyor (USB sürücü bağlı değil veya Seri No uyuşmuyor)."
                    };

                    await _historyRepository.AddAsync(usbHistoryEntry);
                    _notificationService.ShowNotification("Terabithia Sync - Hedef Sürücü Bekleniyor", $"'{job.Name}' görevi için hedef USB sürücüsü bulunamadı.", NotificationType.Warning);
                    return;
                }

                // If volume serial is specified, resolve drive letter if it changed
                if (!string.IsNullOrEmpty(job.TargetVolumeSerialNumber))
                {
                    string? currentPathSerial = _usbDriveService.GetVolumeSerialNumber(job.DestinationPath);
                    if (!string.Equals(currentPathSerial, job.TargetVolumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        string? newLetter = _usbDriveService.FindDriveLetterByVolumeSerialNumber(job.TargetVolumeSerialNumber);
                        if (!string.IsNullOrEmpty(newLetter))
                        {
                            string oldRoot = Path.GetPathRoot(job.DestinationPath) ?? "";
                            string subPath = job.DestinationPath.Substring(oldRoot.Length);
                            string resolvedPath = Path.Combine(newLetter, subPath);
                            _logService.LogInformation($"[EXECUTION TRACE] USB sürücü harfi güncellendi: '{job.DestinationPath}' -> '{resolvedPath}' (Seri No: {job.TargetVolumeSerialNumber})");
                            job.DestinationPath = resolvedPath;
                        }
                    }
                }
            }

            // Preflight Validation Check
            if (_preflightValidationService != null)
            {
                var preflightResult = await _preflightValidationService.ValidateJobAsync(job);
                if (preflightResult.HasBlockingErrors || (preflightResult.HasWarnings && preflightResult.Issues.Any(i => i.Code == PreflightIssueCode.SOURCE_INSIDE_DESTINATION)))
                {
                    var firstIssue = preflightResult.Issues.FirstOrDefault();
                    string reasonMsg = firstIssue != null ? $"{firstIssue.Title}: {firstIssue.Message}" : "Ön kontrol (preflight) doğrulama hatası.";

                    _logService.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Preflight engelledi '{job.Name}': {reasonMsg}");

                    var preflightHistoryEntry = new HistoryEntry
                    {
                        JobId = job.Id,
                        JobName = job.Name,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = JobResultStatus.Skipped,
                        Message = $"Ön Kontrol Engelledi: {reasonMsg}"
                    };

                    await _historyRepository.AddAsync(preflightHistoryEntry);
                    _notificationService.ShowNotification("Terabithia Sync - Ön Kontrol Hatası", $"'{job.Name}' görevi ön kontrolü geçemedi: {reasonMsg}", NotificationType.Warning);
                    return;
                }
            }

            JobCheckpoint? resumeCheckpoint = null;
            if ((isRecoveryResume || source == ExecutionTriggerSource.HistoryRetry || source == ExecutionTriggerSource.HistoryRetrySelected) && _checkpointRepository != null)
            {
                resumeCheckpoint = await _checkpointRepository.GetCheckpointAsync(job.Id);
                if (resumeCheckpoint != null)
                {
                    _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] Checkpoint devralınıyor: JobId={job.Id}, CheckpointState={resumeCheckpoint.CurrentState}");
                }
            }

            _logService.LogInformation($"[EXECUTION STARTING] JobId={job.Id}, Name='{job.Name}', Source={source}, DryRun={dryRun}");
            _logService.LogInformation($"[ACTIVE CARD TRACE] JobId={job.Id} TriggerSource={source} Action=Added");
            DateTime startTime = DateTime.Now;

            using var session = _jobExecutionManager?.RegisterSession(job);
            var cancelToken = session != null
                ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, session.CancellationTokenSource.Token).Token
                : context.CancellationToken;
            var pauseToken = session?.PauseTokenSource.Token;
            var progress = session?.Progress;

            try
            {
                FileCopyResult result;
                if (retryFiles != null && retryFiles.Count > 0)
                {
                    result = await _fileCopyService.RetryFailedFilesAsync(job, retryFiles, dryRun, progress, cancelToken, pauseToken, resumeCheckpoint, triggerSource: source);
                }
                else
                {
                    result = await _fileCopyService.CopyAsync(job, dryRun, progress, cancelToken, pauseToken, resumeCheckpoint, triggerSource: source);
                }
                DateTime endTime = DateTime.Now;

                var historyEntry = new HistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    StartTime = startTime,
                    EndTime = endTime,
                    FilesCopied = result.FilesCopied,
                    FilesSkipped = result.FilesSkipped,
                    FilesFailed = result.FilesFailed,
                    BytesCopied = result.BytesCopied,
                    Status = result.Status,
                    Message = result.Success ? "İşlem başarıyla tamamlandı." : "İşlem bazı hatalarla tamamlandı.",
                    Errors = result.Errors,
                    FileResults = result.FileResults
                };

                await _historyRepository.AddAsync(historyEntry);

                job.LastRun = startTime;
                job.LastResult = result.Status;
                await _jobRepository.UpdateAsync(job);

                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] EXECUTION COMPLETED: '{job.Name}'. Status={result.Status}, Copied={result.FilesCopied}, Skipped={result.FilesSkipped}, Failed={result.FilesFailed}");

                _notificationService.ShowJobResultNotification(
                    job.Name,
                    result.Success,
                    result.FilesCopied,
                    result.FilesFailed,
                    historyEntry.Message);
            }
            catch (OperationCanceledException ex)
            {
                DateTime endTime = DateTime.Now;
                _logService.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] EXECUTION STOPPED/CANCELLED BY USER: '{job.Name}'");

                var partialResult = ex.Data["FileCopyResult"] as FileCopyResult;

                var historyEntry = new HistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    StartTime = startTime,
                    EndTime = endTime,
                    Status = JobResultStatus.Cancelled,
                    Message = "Kullanıcı tarafından durduruldu veya iptal edildi.",
                    FilesCopied = partialResult?.FilesCopied ?? 0,
                    FilesSkipped = partialResult?.FilesSkipped ?? 0,
                    FilesFailed = partialResult?.FilesFailed ?? 0,
                    FilesIncomplete = partialResult?.FilesIncomplete ?? 0,
                    BytesCopied = partialResult?.BytesCopied ?? 0,
                    BytesWrittenThisExecution = partialResult?.BytesWrittenThisExecution ?? 0,
                    TotalFilesPlanned = partialResult?.TotalFilesPlanned ?? 0,
                    TotalBytesPlanned = partialResult?.TotalBytesPlanned ?? 0,
                    Errors = partialResult?.Errors ?? new System.Collections.Generic.List<string>(),
                    FileResults = partialResult?.FileResults ?? new System.Collections.Generic.List<FileItemResult>()
                };

                await _historyRepository.AddAsync(historyEntry);

                job.LastRun = startTime;
                job.LastResult = JobResultStatus.Cancelled;
                await _jobRepository.UpdateAsync(job);
            }
            catch (Exception ex)
            {
                DateTime endTime = DateTime.Now;
                _logService.LogError($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [TID:{tid}] EXECUTION FAILED WITH EXCEPTION in '{job.Name}': {ex.Message}", ex);

                var partialResult = ex.Data["FileCopyResult"] as FileCopyResult;

                var historyEntry = new HistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    StartTime = startTime,
                    EndTime = endTime,
                    Status = JobResultStatus.Failure,
                    Message = $"Kritik Hata: {ex.Message}",
                    FilesCopied = partialResult?.FilesCopied ?? 0,
                    FilesSkipped = partialResult?.FilesSkipped ?? 0,
                    FilesFailed = partialResult?.FilesFailed ?? 0,
                    FilesIncomplete = partialResult?.FilesIncomplete ?? 0,
                    BytesCopied = partialResult?.BytesCopied ?? 0,
                    BytesWrittenThisExecution = partialResult?.BytesWrittenThisExecution ?? 0,
                    TotalFilesPlanned = partialResult?.TotalFilesPlanned ?? 0,
                    TotalBytesPlanned = partialResult?.TotalBytesPlanned ?? 0,
                    Errors = partialResult?.Errors ?? new System.Collections.Generic.List<string> { ex.ToString() },
                    FileResults = partialResult?.FileResults ?? new System.Collections.Generic.List<FileItemResult>()
                };

                await _historyRepository.AddAsync(historyEntry);

                job.LastRun = startTime;
                job.LastResult = JobResultStatus.Failure;
                await _jobRepository.UpdateAsync(job);

                _notificationService.ShowJobResultNotification(job.Name, false, historyEntry.FilesCopied, historyEntry.FilesFailed, ex.Message);
            }
            finally
            {
                _logService.LogInformation($"[ACTIVE CARD TRACE] JobId={job.Id} TriggerSource={source} Action=Removed");
            }
        }
    }
}
