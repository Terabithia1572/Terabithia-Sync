using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class FileCopyService : IFileCopyService
    {
        private const int BufferSize = 512 * 1024; // 512 KB streaming buffer for optimal write latency and throughput
        private const int FileStreamInternalBufferSize = 4096; // 4 KB internal FileStream buffer
        private readonly ILogService? _logService;
        private readonly IUsbDriveService? _usbDriveService;
        private readonly ICheckpointRepository? _checkpointRepository;
        private readonly IJobExecutionGate? _jobExecutionGate;
        private readonly IJobExecutionManager? _jobExecutionManager;
        private readonly ISettingsRepository? _settingsRepository;

        public FileCopyService(
            ILogService? logService = null,
            IUsbDriveService? usbDriveService = null,
            ICheckpointRepository? checkpointRepository = null,
            IJobExecutionGate? jobExecutionGate = null,
            IJobExecutionManager? jobExecutionManager = null,
            ISettingsRepository? settingsRepository = null)
        {
            _logService = logService;
            _usbDriveService = usbDriveService;
            _checkpointRepository = checkpointRepository ?? new CheckpointRepository(logService);
            _jobExecutionGate = jobExecutionGate;
            _jobExecutionManager = jobExecutionManager;
            _settingsRepository = settingsRepository;
        }

        private static int _executionSequenceCounter = 0;

        public async Task<FileCopyResult> CopyAsync(
            Job job,
            bool dryRun,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken,
            IPauseToken? pauseToken = null,
            JobCheckpoint? resumeCheckpoint = null,
            ExecutionTriggerSource triggerSource = ExecutionTriggerSource.QuartzScheduled)
        {
            int seq = Interlocked.Increment(ref _executionSequenceCounter);
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            var result = new FileCopyResult { Success = true, Status = JobResultStatus.Success };
            var fileList = new List<FileTaskItem>();
            var sourceRootMappings = new List<(string SourceRoot, string DestSubDir)>();
            bool hasFatalDiscoveryError = false;

            JobCheckpoint? existingCheck = resumeCheckpoint;
            if (existingCheck == null && _checkpointRepository != null)
            {
                existingCheck = await _checkpointRepository.GetCheckpointAsync(job.Id);
            }

            string existingStateStr = existingCheck != null ? existingCheck.CurrentState.ToString() : "None";
            _logService?.LogInformation($"[EXECUTION REQUEST] Sequence={seq} JobId={job.Id} JobName='{job.Name}' TriggerSource={triggerSource} CheckpointState={existingStateStr} DryRun={dryRun} IsResumeCheckpointPassed={resumeCheckpoint != null} PID={pid} TID={tid}");

            // Defense in Depth Safety Net: Refuse execution if an unfinished recoverable checkpoint exists unless triggerSource is RecoveryResume
            if (!dryRun && existingCheck != null && existingCheck.IsRecoverable && existingCheck.CurrentState != ExecutionState.Completed)
            {
                bool isSameProcessSession = existingCheck.ProcessInstanceId.HasValue && existingCheck.ProcessInstanceId.Value == JobExecutionGate.CurrentProcessInstanceId;

                bool allowed = triggerSource == ExecutionTriggerSource.RecoveryResume ||
                               (isSameProcessSession && triggerSource == ExecutionTriggerSource.DestinationReturnedSameActiveSession && existingCheck.GetEffectiveInterruptionReason() == ExecutionInterruptionReason.DestinationUnavailable);

                if (!allowed)
                {
                    _logService?.LogWarning($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] [ProcessInstanceId:{JobExecutionGate.CurrentProcessInstanceId}] [TID:{tid}] FileCopyService REFUSED EXECUTION (Sequence={seq}): JobId={job.Id} has existing recoverable checkpoint (State={existingCheck.CurrentState}, Reason={existingCheck.GetEffectiveInterruptionReason()}, CheckpointProcessId={existingCheck.ProcessInstanceId}) but TriggerSource={triggerSource} is NOT RecoveryResume!");
                    result.Success = false;
                    result.Status = JobResultStatus.Cancelled;
                    result.Errors.Add($"Tamamlanmamış veya durdurulmuş kopyalama kaydı mevcut ({existingCheck.CurrentState}); doğrudan kopyalama reddedildi.");
                    return result;
                }
            }

            if (job.SourcePaths == null || !job.SourcePaths.Any())
            {
                result.Success = false;
                result.Status = JobResultStatus.Failure;
                result.Errors.Add("Kaynak yollar belirtilmedi.");
                _logService?.LogError("Kaynak yollar belirtilmedi.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(job.DestinationPath))
            {
                result.Success = false;
                result.Status = JobResultStatus.Failure;
                result.Errors.Add("Hedef klasör yolu belirtilmedi.");
                _logService?.LogError("Hedef klasör yolu belirtilmedi.");
                return result;
            }

            var currentSettings = _settingsRepository != null ? await _settingsRepository.GetAsync() : new Settings();
            int effectiveCopyBufferSize = currentSettings.GetValidatedCopyBufferSize();

            string fullDest = Path.GetFullPath(job.DestinationPath);
            _logService?.LogInformation($"'{(string.IsNullOrWhiteSpace(job.Name) ? "Kopyalama Görevi" : job.Name)}' görevi kopyalama işlemi başlatılıyor.");
            _logService?.LogInformation($"Hedef hazırlanıyor: {fullDest}");

            // Ensure destination drive is available before starting, or wait
            await EnsureDestinationDriveAvailableAsync(fullDest, progress, cancellationToken, job, null);

            // Step 1: Discover all files preserving source directory roots
            foreach (var src in job.SourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(src)) continue;

                try
                {
                    string fullSrc = Path.GetFullPath(src);
                    _logService?.LogInformation($"Kaynak hazırlanıyor: {fullSrc}");

                    if (Directory.Exists(fullSrc))
                    {
                        string folderName = Path.GetFileName(fullSrc.TrimEnd('\\', '/'));
                        if (string.IsNullOrEmpty(folderName))
                        {
                            folderName = "RootFolder";
                        }

                        string targetSubDir = Path.Combine(fullDest, folderName);
                        sourceRootMappings.Add((fullSrc, targetSubDir));

                        // Check same location
                        if (string.Equals(fullSrc.TrimEnd('\\', '/'), targetSubDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Kaynak ve hedef aynı konum olamaz: '{src}'");
                            hasFatalDiscoveryError = true;
                            continue;
                        }

                        // Check destination inside source recursion
                        if (targetSubDir.StartsWith(fullSrc.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Hedef klasör, kaynak klasörün alt klasörü olamaz: '{fullSrc}' -> '{targetSubDir}'");
                            hasFatalDiscoveryError = true;
                            continue;
                        }

                        var dirInfo = new DirectoryInfo(fullSrc);
                        var files = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var relative = Path.GetRelativePath(fullSrc, file.FullName);
                            var destFile = Path.Combine(targetSubDir, relative);
                            fileList.Add(new FileTaskItem
                            {
                                SourcePath = file.FullName,
                                DestinationPath = destFile,
                                Length = file.Length,
                                LastWriteTime = file.LastWriteTimeUtc
                            });
                        }
                    }
                    else if (File.Exists(fullSrc))
                    {
                        var file = new FileInfo(fullSrc);
                        var destFile = Path.Combine(fullDest, file.Name);

                        if (string.Equals(fullSrc, destFile, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Kaynak ve hedef dosya aynı konumda: '{src}'");
                            hasFatalDiscoveryError = true;
                            continue;
                        }

                        fileList.Add(new FileTaskItem
                        {
                            SourcePath = file.FullName,
                            DestinationPath = destFile,
                            Length = file.Length,
                            LastWriteTime = file.LastWriteTimeUtc
                        });
                    }
                    else
                    {
                        result.Errors.Add($"Kaynak bulunamadı: '{src}'");
                        hasFatalDiscoveryError = true;
                        if (!job.ContinueOnError)
                        {
                            result.Success = false;
                            result.Status = JobResultStatus.Failure;
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Kaynak taranırken hata: '{src}' - {ex.Message}");
                    hasFatalDiscoveryError = true;
                    if (!job.ContinueOnError)
                    {
                        result.Success = false;
                        result.Status = JobResultStatus.Failure;
                        return result;
                    }
                }
            }

            if (hasFatalDiscoveryError && !job.ContinueOnError)
            {
                result.Success = false;
                result.Status = JobResultStatus.Failure;
                return result;
            }

            // Step 2: Handle Mirror Deletion (Scoped strictly to target subdirectories)
            if (job.CopyMode == CopyMode.Mirror && job.EnableMirrorDeletion && !dryRun)
            {
                foreach (var (srcRoot, targetSubDir) in sourceRootMappings)
                {
                    if (Directory.Exists(targetSubDir))
                    {
                        try
                        {
                            var validDestFiles = new HashSet<string>(
                                fileList.Where(f => f.DestinationPath.StartsWith(targetSubDir, StringComparison.OrdinalIgnoreCase))
                                        .Select(f => Path.GetFullPath(f.DestinationPath)),
                                StringComparer.OrdinalIgnoreCase);

                            var destDirInfo = new DirectoryInfo(targetSubDir);
                            foreach (var existingDestFile in destDirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (!validDestFiles.Contains(existingDestFile.FullName))
                                {
                                    try
                                    {
                                        if (existingDestFile.IsReadOnly)
                                            existingDestFile.IsReadOnly = false;
                                        existingDestFile.Delete();
                                        _logService?.LogInformation($"Ayna modu: Hedefte fazlalık dosya silindi: '{existingDestFile.FullName}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        result.Errors.Add($"Ayna modu silme hatası '{existingDestFile.FullName}': {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Ayna temizliği sırasında hata ('{targetSubDir}'): {ex.Message}");
                        }
                    }
                }
            }

            long totalBytes = fileList.Sum(f => f.Length);
            int totalFiles = fileList.Count;
            long currentBytesCopied = 0;
            int filesCopied = 0;
            int filesSkipped = 0;
            int filesFailed = 0;
            int filesIncomplete = 0;

            result.TotalFilesPlanned = totalFiles;
            result.TotalBytesPlanned = totalBytes;

            if (job.CreateDestinationIfMissing && !dryRun && !Directory.Exists(fullDest))
            {
                try
                {
                    Directory.CreateDirectory(fullDest);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Status = JobResultStatus.Failure;
                    result.Errors.Add($"Hedef klasör oluşturulamadı: {ex.Message}");
                    _logService?.LogError($"Hedef klasör oluşturulamadı: {ex.Message}");
                    return result;
                }
            }

            _logService?.LogInformation($"Kopyalama işlemi başlatıldı. Toplam dosya: {totalFiles}, Toplam boyut: {FormatBytes(totalBytes)}");

            if (resumeCheckpoint != null)
            {
                int cpCompleted = resumeCheckpoint.FileEntries?.Count(e => e.Status == CheckpointFileStatus.Completed || e.Status == CheckpointFileStatus.Skipped) ?? 0;
                int cpPending = resumeCheckpoint.FileEntries?.Count(e => e.Status == CheckpointFileStatus.Pending) ?? 0;
                int cpFailed = resumeCheckpoint.FileEntries?.Count(e => e.Status == CheckpointFileStatus.Failed) ?? 0;
                int cpSkipped = resumeCheckpoint.FileEntries?.Count(e => e.Status == CheckpointFileStatus.Skipped) ?? 0;
                string currFile = resumeCheckpoint.CurrentFile ?? "None";

                _logService?.LogInformation($"[RECOVERY RESUME] JobId={job.Id} CheckpointPath='{job.DestinationPath}' TotalCheckpointEntries={resumeCheckpoint.FileEntries?.Count ?? 0} CompletedEntries={cpCompleted} PendingEntries={cpPending} FailedEntries={cpFailed} SkippedEntries={cpSkipped} CurrentFile='{currFile}'");
            }

            var throttler = new ProgressThrottler(progress, job, totalFiles, totalBytes, validatedBaselineBytes: resumeCheckpoint?.CompletedBytes ?? 0);
            throttler.ReportStart();

            // Checkpoint initialization & state tracking
            JobCheckpoint? checkpoint = null;
            if (!dryRun)
            {
                checkpoint = resumeCheckpoint;

                if (checkpoint == null)
                {
                    checkpoint = new JobCheckpoint
                    {
                        SchemaVersion = 1,
                        JobId = job.Id,
                        JobName = job.Name ?? string.Empty,
                        CreatedAt = DateTime.Now,
                        ActualStartTime = DateTime.Now,
                        SourcePaths = job.SourcePaths.ToList(),
                        DestinationPath = job.DestinationPath,
                        CopyMode = job.CopyMode,
                        ConflictPolicy = job.ConflictPolicy,
                        VerificationMode = job.VerificationMode,
                        CurrentState = ExecutionState.Running,
                        IsRecoverable = true,
                        ProcessInstanceId = JobExecutionGate.CurrentProcessInstanceId
                    };
                }

                checkpoint.CurrentState = ExecutionState.Running;
                checkpoint.TotalFiles = totalFiles;
                checkpoint.TotalBytes = totalBytes;
                checkpoint.ProcessInstanceId = JobExecutionGate.CurrentProcessInstanceId;
                checkpoint.UpdatedAt = DateTime.Now;
                await SaveCheckpointSafeAsync(checkpoint);
            }

            var cpEntryMap = checkpoint?.FileEntries != null
                ? checkpoint.FileEntries.ToDictionary(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CheckpointFileEntry>(StringComparer.OrdinalIgnoreCase);

            var updatedCpEntries = new List<CheckpointFileEntry>();

            // Step 3: Process files with USB resilience, checkpoint validation, and temporary file streaming
            try
            {
                for (int i = 0; i < fileList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (pauseToken != null && pauseToken.IsPaused)
                    {
                        if (checkpoint != null)
                        {
                            checkpoint.CurrentState = ExecutionState.Paused;
                            checkpoint.InterruptionReasonCode = ExecutionInterruptionReason.UserPaused;
                            checkpoint.StatusMessage = "Duraklatıldı";
                            await SaveCheckpointSafeAsync(checkpoint);
                        }
                        throttler.ReportState(ExecutionState.Paused, "Duraklatıldı");
                        await pauseToken.WaitWhilePausedAsync(cancellationToken);
                        if (checkpoint != null)
                        {
                            checkpoint.CurrentState = ExecutionState.Running;
                            checkpoint.StatusMessage = "Kopyalanıyor...";
                            await SaveCheckpointSafeAsync(checkpoint);
                        }
                        throttler.ReportState(ExecutionState.Running, null);
                    }

                    var item = fileList[i];
                    throttler.ProgressData.CurrentFileName = Path.GetFileName(item.SourcePath);
                    throttler.ProgressData.CurrentFilePath = item.SourcePath;

                    var fileResultItem = new FileItemResult
                    {
                        SourcePath = item.SourcePath,
                        DestinationPath = item.DestinationPath,
                        RelativePath = Path.GetRelativePath(fullDest, item.DestinationPath),
                        FileName = Path.GetFileName(item.SourcePath),
                        FileSize = item.Length,
                        BytesTransferred = 0,
                        Status = FileItemStatus.Pending,
                        Timestamp = DateTime.Now,
                        StartedAt = DateTime.Now
                    };
                    long currentFileBytesWritten = 0;

                    // Checkpoint file entry matching & completed-file validation
                    CheckpointFileEntry? cpEntry = null;
                    if (cpEntryMap.TryGetValue(item.SourcePath, out var existingCpEntry))
                    {
                        cpEntry = existingCpEntry;
                        if (cpEntry.Status == CheckpointFileStatus.Completed || cpEntry.Status == CheckpointFileStatus.Skipped)
                        {
                            bool isValid = ValidateCompletedFile(item, cpEntry, job.VerificationMode, job.VerifyCopy);
                            if (isValid)
                            {
                                filesSkipped++;
                                currentBytesCopied += item.Length;
                                fileResultItem.Status = FileItemStatus.Skipped;
                                fileResultItem.BytesTransferred = item.Length;
                                fileResultItem.EndedAt = DateTime.Now;
                                result.FileResults.Add(fileResultItem);
                                _logService?.LogInformation($"[Kurtarma] Önceden tamamlanmış ve doğrulanmış dosya atlandı: {Path.GetFileName(item.SourcePath)}");

                                updatedCpEntries.Add(cpEntry);
                                if (checkpoint != null)
                                {
                                    checkpoint.CompletedFiles = filesCopied + filesSkipped;
                                    checkpoint.CompletedBytes = currentBytesCopied;
                                }
                                continue;
                            }
                            else
                            {
                                _logService?.LogWarning($"[Kurtarma] Tamamlanmış dosya doğrulaması başarısız veya kaynak değişmiş: '{Path.GetFileName(item.SourcePath)}'. Yeniden kopyalanıyor.");
                                cpEntry.Status = CheckpointFileStatus.Pending;
                                cpEntry.BytesCopied = 0;
                            }
                        }
                        else if (cpEntry.Status == CheckpointFileStatus.Copying)
                        {
                            cpEntry.Status = CheckpointFileStatus.Pending;
                            cpEntry.BytesCopied = 0;
                        }
                    }

                    if (cpEntry == null)
                    {
                        cpEntry = new CheckpointFileEntry
                        {
                            SourcePath = item.SourcePath,
                            DestinationPath = item.DestinationPath,
                            RelativePath = Path.GetRelativePath(fullDest, item.DestinationPath),
                            SourceLength = item.Length,
                            SourceLastWriteTimeUtc = item.LastWriteTime,
                            Status = CheckpointFileStatus.Pending
                        };
                    }
                    updatedCpEntries.Add(cpEntry);

                    throttler.ReportChunk(0, 0, item.Length);

                    // Ensure drive is connected before each file
                    await EnsureDestinationDriveAvailableAsync(item.DestinationPath, progress, cancellationToken, job, throttler);

                    // Mode: VerifyOnly
                    if (job.CopyMode == CopyMode.VerifyOnly)
                    {
                        if (!File.Exists(item.DestinationPath))
                        {
                            filesFailed++;
                            fileResultItem.Status = FileItemStatus.Failed;
                            fileResultItem.ErrorMessage = "Hedef dosya bulunamadı.";
                            result.Errors.Add($"Doğrulama hatası: Hedef dosya yok '{item.DestinationPath}'");
                            _logService?.LogWarning($"Doğrulama hatası: Hedef dosya yok '{item.DestinationPath}'");
                        }
                        else
                        {
                            bool match = CheckFilesMatch(item.SourcePath, item.DestinationPath, job.VerifyCopy);
                            if (match)
                            {
                                filesSkipped++;
                                fileResultItem.Status = FileItemStatus.Skipped;
                                fileResultItem.BytesTransferred = item.Length;
                                _logService?.LogInformation($"Dosya zaten hedefte ve doğrulandı: {Path.GetFileName(item.SourcePath)}");
                            }
                            else
                            {
                                filesFailed++;
                                fileResultItem.Status = FileItemStatus.Failed;
                                fileResultItem.ErrorMessage = "Doğrulama uyuşmazlığı.";
                                result.Errors.Add($"Doğrulama uyuşmazlığı: '{item.SourcePath}' vs '{item.DestinationPath}'");
                                _logService?.LogWarning($"Doğrulama uyuşmazlığı: '{item.SourcePath}' vs '{item.DestinationPath}'");
                            }
                        }
                        fileResultItem.EndedAt = DateTime.Now;
                        result.FileResults.Add(fileResultItem);
                        continue;
                    }

                    // Check conflict / skipping logic
                    bool destExists = File.Exists(item.DestinationPath);
                    if (destExists)
                    {
                        if (job.ConflictPolicy == ConflictPolicy.Skip)
                        {
                            filesSkipped++;
                            currentBytesCopied += item.Length;
                            fileResultItem.Status = FileItemStatus.Skipped;
                            fileResultItem.BytesTransferred = item.Length;
                            fileResultItem.EndedAt = DateTime.Now;
                            result.FileResults.Add(fileResultItem);
                            cpEntry.Status = CheckpointFileStatus.Skipped;
                            if (checkpoint != null)
                            {
                                checkpoint.SkippedFiles = filesSkipped;
                                checkpoint.CompletedBytes = currentBytesCopied;
                                await SaveCheckpointSafeAsync(checkpoint);
                            }
                            _logService?.LogInformation($"Dosya zaten hedefte ve atlandı: {Path.GetFileName(item.SourcePath)}");
                            continue;
                        }
                        else if (job.CopyMode == CopyMode.Incremental && job.ConflictPolicy != ConflictPolicy.Rename)
                        {
                            bool isSame = CheckIsSameFile(item, job.VerifyCopy);
                            if (isSame)
                            {
                                filesSkipped++;
                                currentBytesCopied += item.Length;
                                fileResultItem.Status = FileItemStatus.Skipped;
                                fileResultItem.BytesTransferred = item.Length;
                                fileResultItem.EndedAt = DateTime.Now;
                                result.FileResults.Add(fileResultItem);
                                cpEntry.Status = CheckpointFileStatus.Skipped;
                                if (checkpoint != null)
                                {
                                    checkpoint.SkippedFiles = filesSkipped;
                                    checkpoint.CompletedBytes = currentBytesCopied;
                                    await SaveCheckpointSafeAsync(checkpoint);
                                }
                                _logService?.LogInformation($"Dosya zaten hedefte ve doğrulandı: {Path.GetFileName(item.SourcePath)}");
                                continue;
                            }
                        }
                    }

                    string targetPath = item.DestinationPath;
                    if (destExists && job.ConflictPolicy == ConflictPolicy.Rename)
                    {
                        targetPath = GetUniqueFilePath(item.DestinationPath);
                        fileResultItem.DestinationPath = targetPath;
                    }

                    if (dryRun)
                    {
                        filesCopied++;
                        currentBytesCopied += item.Length;
                        fileResultItem.Status = FileItemStatus.Completed;
                        fileResultItem.BytesTransferred = item.Length;
                        fileResultItem.EndedAt = DateTime.Now;
                        result.FileResults.Add(fileResultItem);
                        _logService?.LogInformation($"[Ön İzleme] Dosya kopyalandı sayıldı: {Path.GetFileName(item.SourcePath)}");
                        continue;
                    }

                    // Execute file copy with USB interruption retry / resume logic
                    int retries = Math.Max(1, job.RetryCount);

                    cpEntry.Status = CheckpointFileStatus.Copying;
                    cpEntry.Attempts++;
                    if (checkpoint != null)
                    {
                        checkpoint.CurrentFile = Path.GetFileName(item.SourcePath);
                        checkpoint.CurrentFileTotalBytes = item.Length;
                        checkpoint.CurrentFileBytesCopied = 0;
                        checkpoint.FileEntries = updatedCpEntries;
                        await SaveCheckpointSafeAsync(checkpoint);
                    }

                    for (int attempt = 1; attempt <= retries; attempt++)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        catch (OperationCanceledException)
                        {
                            if (checkpoint != null)
                            {
                                checkpoint.CurrentState = pauseToken?.IsPaused == true ? ExecutionState.Paused : ExecutionState.Cancelled;
                                checkpoint.InterruptionReason = "Kullanıcı tarafından durduruldu veya iptal edildi.";
                                await SaveCheckpointSafeAsync(checkpoint);
                            }
                            throw;
                        }

                        fileResultItem.RetryCount = attempt - 1;
                        try
                        {
                            var targetDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }

                            if (File.Exists(targetPath))
                            {
                                var targetFi = new FileInfo(targetPath);
                                if (targetFi.IsReadOnly) targetFi.IsReadOnly = false;
                            }

                            // Safe temporary file streaming (.tmp)
                            string tempPath = targetPath + ".tmp";
                            if (File.Exists(tempPath))
                            {
                                try { File.Delete(tempPath); } catch { }
                            }

                            await CopyFileStreamAsync(
                                item.SourcePath,
                                tempPath,
                                item.Length,
                                null,
                                pauseToken,
                                throttler,
                                cancellationToken,
                                onChunkWritten: bytesWritten =>
                                {
                                    currentFileBytesWritten = bytesWritten;
                                    fileResultItem.BytesTransferred = bytesWritten;
                                });

                            var srcFi = new FileInfo(item.SourcePath);
                            var tempFiInfo = new FileInfo(tempPath);

                            if (job.PreserveTimestamps)
                            {
                                tempFiInfo.LastWriteTimeUtc = srcFi.LastWriteTimeUtc;
                                tempFiInfo.CreationTimeUtc = srcFi.CreationTimeUtc;
                            }

                            if (job.PreserveAttributes)
                            {
                                tempFiInfo.Attributes = srcFi.Attributes;
                            }

                            if (job.VerifyCopy)
                            {
                                bool valid = CompareFileHashes(item.SourcePath, tempPath);
                                if (!valid)
                                {
                                    try { File.Delete(tempPath); } catch { }
                                    throw new IOException("SHA-256 doğrulama başarısız oldu.");
                                }
                            }

                            // Atomic move from .tmp to final targetPath
                            if (File.Exists(targetPath))
                            {
                                File.Delete(targetPath);
                            }
                            File.Move(tempPath, targetPath);

                            if (job.PreserveTimestamps)
                            {
                                try { File.SetLastWriteTimeUtc(targetPath, item.LastWriteTime); } catch { }
                            }

                            filesCopied++;
                            currentBytesCopied += item.Length;
                            fileResultItem.Status = FileItemStatus.Completed;
                            fileResultItem.BytesTransferred = item.Length;
                            fileResultItem.ErrorMessage = null;
                            fileResultItem.EndedAt = DateTime.Now;

                            cpEntry.Status = CheckpointFileStatus.Completed;
                            cpEntry.BytesCopied = item.Length;
                            cpEntry.CompletedAt = DateTime.Now;

                            if (checkpoint != null)
                            {
                                checkpoint.CompletedFiles = filesCopied;
                                checkpoint.CompletedBytes = currentBytesCopied;
                                checkpoint.PendingFiles = Math.Max(0, checkpoint.TotalFiles - (filesCopied + filesSkipped + filesFailed));
                                await SaveCheckpointSafeAsync(checkpoint);
                            }

                            _logService?.LogInformation($"Dosya başarıyla kopyalandı: {Path.GetFileName(item.SourcePath)}");
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            fileResultItem.EndedAt = DateTime.Now;
                            if (checkpoint != null)
                            {
                                if (checkpoint.CurrentState != ExecutionState.Stopped && checkpoint.InterruptionReasonCode != ExecutionInterruptionReason.UserStopped)
                                {
                                    checkpoint.CurrentState = ExecutionState.Cancelled;
                                    checkpoint.InterruptionReasonCode = ExecutionInterruptionReason.UserCancelled;
                                    checkpoint.InterruptionReason = "Kullanıcı tarafından iptal edildi.";
                                }
                                await SaveCheckpointSafeAsync(checkpoint);
                            }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            string friendlyErr = UserFriendlyErrorTranslator.Translate(ex);
                            fileResultItem.ErrorMessage = friendlyErr;

                            // Check if error is due to drive disconnection
                            if (IsDriveDisconnectedException(ex) || !IsDriveConnected(targetPath))
                            {
                                fileResultItem.Status = FileItemStatus.WaitingForDestination;
                                _logService?.LogWarning("USB sürücüsü çıkarıldı. Kopyalama işlemi beklemeye alındı.");

                                if (checkpoint != null)
                                {
                                    checkpoint.CurrentState = ExecutionState.DestinationUnavailable;
                                    checkpoint.DestinationWasUnavailable = true;
                                    checkpoint.InterruptionReason = "Hedef USB sürücüsünün bağlantısı kesildi.";
                                    await SaveCheckpointSafeAsync(checkpoint);
                                }

                                await EnsureDestinationDriveAvailableAsync(targetPath, progress, cancellationToken, job, throttler);
                                _logService?.LogInformation("Bekleyen dosyalar yeniden kopyalanıyor.");
                                attempt--; // Reset attempt to retry this exact file after drive returns
                                continue;
                            }

                            if (attempt == retries)
                            {
                                filesFailed++;
                                fileResultItem.Status = FileItemStatus.Failed;
                                fileResultItem.EndedAt = DateTime.Now;
                                result.Errors.Add($"'{Path.GetFileName(item.SourcePath)}': {friendlyErr}");
                                _logService?.LogError($"Dosya kopyalanamadı ('{Path.GetFileName(item.SourcePath)}'): {friendlyErr}");

                                cpEntry.Status = CheckpointFileStatus.Failed;
                                cpEntry.LastError = friendlyErr;

                                if (checkpoint != null)
                                {
                                    checkpoint.FailedFiles = filesFailed;
                                    checkpoint.LastError = friendlyErr;
                                    checkpoint.PendingFiles = Math.Max(0, checkpoint.TotalFiles - (filesCopied + filesSkipped + filesFailed));
                                    await SaveCheckpointSafeAsync(checkpoint);
                                }

                                if (!job.ContinueOnError)
                                {
                                    result.Success = false;
                                    result.Status = JobResultStatus.Failure;
                                    break;
                                }
                            }
                            else
                            {
                                fileResultItem.Status = FileItemStatus.Retrying;
                                try
                                {
                                    await Task.Delay(job.RetryDelay > TimeSpan.Zero ? job.RetryDelay : TimeSpan.FromSeconds(1), cancellationToken);
                                }
                                catch (OperationCanceledException) { throw; }
                            }
                        }
                    }

                    result.FileResults.Add(fileResultItem);

                    if (!result.Success && !job.ContinueOnError)
                        break;
                }
            }
            catch (OperationCanceledException cancelEx)
            {
                _logService?.LogInformation($"[EXECUTION CANCELED HANDLER] Capturing partial execution history before throwing.");

                // If a file was in progress when cancellation occurred, finalize its result
                for (int i = 0; i < fileList.Count; i++)
                {
                    var file = fileList[i];
                    var existingRes = result.FileResults.FirstOrDefault(r => string.Equals(r.SourcePath, file.SourcePath, StringComparison.OrdinalIgnoreCase));
                    if (existingRes == null)
                    {
                        // Check if this was the current file being copied when cancelled
                        if (result.FileResults.Count == i)
                        {
                            // File was partially written
                            var partialItem = new FileItemResult
                            {
                                SourcePath = file.SourcePath,
                                DestinationPath = file.DestinationPath,
                                RelativePath = Path.GetRelativePath(fullDest, file.DestinationPath),
                                FileName = Path.GetFileName(file.SourcePath),
                                FileSize = file.Length,
                                Timestamp = DateTime.Now,
                                EndedAt = DateTime.Now
                            };

                            // Check temp file size if available
                            string tempPath = file.DestinationPath + ".tmp";
                            long written = 0;
                            if (File.Exists(tempPath))
                            {
                                try { written = new FileInfo(tempPath).Length; } catch { }
                            }

                            if (written > 0 && written < file.Length)
                            {
                                partialItem.Status = FileItemStatus.Incomplete;
                                partialItem.BytesTransferred = written;
                                partialItem.ErrorMessage = "Kopyalama işlemi durduruldu (Yarım kaldı).";
                                filesIncomplete++;
                                currentBytesCopied += written;
                            }
                            else
                            {
                                partialItem.Status = FileItemStatus.Cancelled;
                                partialItem.BytesTransferred = 0;
                                partialItem.ErrorMessage = "İşlem kullanıcı tarafından iptal edildi.";
                            }
                            result.FileResults.Add(partialItem);
                        }
                        else
                        {
                            result.FileResults.Add(new FileItemResult
                            {
                                SourcePath = file.SourcePath,
                                DestinationPath = file.DestinationPath,
                                RelativePath = Path.GetRelativePath(fullDest, file.DestinationPath),
                                FileName = Path.GetFileName(file.SourcePath),
                                FileSize = file.Length,
                                BytesTransferred = 0,
                                Status = FileItemStatus.Cancelled,
                                ErrorMessage = "İptal nedeniyle bu dosya kopyalanmadı.",
                                Timestamp = DateTime.Now
                            });
                        }
                    }
                }

                result.FilesCopied = filesCopied;
                result.FilesSkipped = filesSkipped;
                result.FilesFailed = filesFailed;
                result.FilesIncomplete = filesIncomplete;
                result.BytesCopied = currentBytesCopied;
                result.BytesWrittenThisExecution = currentBytesCopied;
                result.Status = JobResultStatus.Cancelled;
                result.Success = false;

                cancelEx.Data["FileCopyResult"] = result;
                throw;
            }

            result.FilesCopied = filesCopied;
            result.FilesSkipped = filesSkipped;
            result.FilesFailed = filesFailed;
            result.BytesCopied = currentBytesCopied;

            if (hasFatalDiscoveryError || filesFailed > 0)
            {
                result.Success = (filesCopied > 0 && !hasFatalDiscoveryError);
                result.Status = (filesCopied > 0 && !hasFatalDiscoveryError) ? JobResultStatus.PartialSuccess : JobResultStatus.Failure;
            }
            else
            {
                result.Success = true;
                result.Status = JobResultStatus.Success;
            }

            _logService?.LogInformation($"Kopyalama işlemi tamamlandı. Aktarılan veri: {FormatBytes(currentBytesCopied)}, Kopyalanan: {filesCopied}, Atlanan: {filesSkipped}, Hatalı: {filesFailed}");

            throttler.ReportFinal(result.Success, result.Success ? "Kopyalama işlemi tamamlandı." : "İşlem bazı hatalarla tamamlandı.");

            // Cleanup checkpoint on successful completion
            if (!dryRun && _checkpointRepository != null)
            {
                if (result.Success)
                {
                    await _checkpointRepository.DeleteCheckpointAsync(job.Id);
                }
                else if (checkpoint != null)
                {
                    checkpoint.CurrentState = ExecutionState.Failed;
                    checkpoint.IsRecoverable = true;
                    await SaveCheckpointSafeAsync(checkpoint);
                }
            }

            return result;
        }

        public async Task<FileCopyResult> CopyAsync(
            IEnumerable<string> sourcePaths,
            string destinationPath,
            CopyMode copyMode,
            ConflictPolicy conflictPolicy,
            bool dryRun,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var dummyJob = new Job
            {
                SourcePaths = sourcePaths.ToList(),
                DestinationPath = destinationPath,
                CopyMode = copyMode,
                ConflictPolicy = conflictPolicy,
                ContinueOnError = true
            };

            var innerProgress = progress != null ? new Progress<FileCopyProgress>(p => progress.Report(p.Percentage)) : null;
            return await CopyAsync(dummyJob, dryRun, innerProgress, cancellationToken);
        }

        private async Task EnsureDestinationDriveAvailableAsync(
            string path,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken,
            Job? job = null,
            ProgressThrottler? throttler = null)
        {
            if (IsDriveConnected(path)) return;

            _logService?.LogWarning($"USB sürücüsü çıkarıldı ('{path}'). Kopyalama işlemi beklemeye alındı.");

            if (throttler != null)
            {
                throttler.ReportState(ExecutionState.DestinationUnavailable, "Hedef sürücü bekleniyor");
            }
            else if (progress != null && job != null)
            {
                progress.Report(new FileCopyProgress
                {
                    JobId = job.Id,
                    JobName = job.Name ?? string.Empty,
                    State = ExecutionState.DestinationUnavailable,
                    StatusMessage = "Hedef sürücü bekleniyor",
                    IsWaitingForUsb = true
                });
            }

            int[] backoffDelaysMs = new int[] { 2000, 3000, 5000, 10000, 15000 };
            int backoffIndex = 0;

            while (!IsDriveConnected(path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (throttler != null)
                {
                    throttler.ReportState(ExecutionState.DestinationUnavailable, "Hedef sürücü bekleniyor");
                }
                else if (progress != null && job != null)
                {
                    progress.Report(new FileCopyProgress
                    {
                        JobId = job.Id,
                        JobName = job.Name ?? string.Empty,
                        State = ExecutionState.DestinationUnavailable,
                        StatusMessage = "Hedef sürücü bekleniyor",
                        IsWaitingForUsb = true
                    });
                }

                int delay = backoffDelaysMs[Math.Min(backoffIndex, backoffDelaysMs.Length - 1)];
                backoffIndex++;

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
            }

            _logService?.LogInformation("USB sürücüsü yeniden algılandı. Kopyalama işlemine devam ediliyor.");
            if (throttler != null)
            {
                throttler.ReportState(ExecutionState.Running, "Çalışıyor...");
            }
        }

        private bool IsDriveConnected(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (_usbDriveService != null)
                {
                    return _usbDriveService.IsDriveConnected(path);
                }

                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return false;
                var drive = new DriveInfo(root);
                return drive.IsReady && Directory.Exists(root);
            }
            catch
            {
                return false;
            }
        }

        private bool IsDriveDisconnectedException(Exception ex)
        {
            if (ex is DirectoryNotFoundException || ex is DriveNotFoundException) return true;
            if (ex is IOException ioEx)
            {
                string msg = ioEx.Message.ToLowerInvariant();
                return msg.Contains("bulunamadı") || msg.Contains("ready") || msg.Contains("device") || msg.Contains("sürücü") || msg.Contains("konum");
            }
            return false;
        }

        private async Task CopyFileStreamAsync(
            string sourcePath,
            string destPath,
            long fileSize,
            IBandwidthLimiter? bandwidthLimiter,
            IPauseToken? pauseToken,
            ProgressThrottler? progressThrottler,
            CancellationToken cancellationToken,
            Action<long>? onChunkWritten = null,
            int customBufferSize = BufferSize)
        {
            int currentBufferSize = customBufferSize > 0 ? customBufferSize : BufferSize;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(currentBufferSize);

            long currentFileBytesCopied = 0;
            try
            {
                long next16MiBBoundary = 16 * 1024 * 1024L;

                // Log Bandwidth Limiter configuration and stream options
                bool isLimiterEnabled = bandwidthLimiter?.IsEnabled ?? false;
                double limitSpeed = bandwidthLimiter?.MegabytesPerSecond ?? 0;
                string bwMode = isLimiterEnabled ? $"Limited ({limitSpeed:F1} MB/s)" : "Unlimited";

                _logService?.LogInformation($"[COPY CONFIG] AppBufferBytes={currentBufferSize} AppBufferKiB={currentBufferSize / 1024} FileStreamBufferBytes={FileStreamInternalBufferSize} SequentialScan=True Async=True BandwidthMode={bwMode}");
                _logService?.LogInformation($"[BANDWIDTH CONFIG] Mode={(isLimiterEnabled ? "Limited" : "Unlimited")} Enabled={isLimiterEnabled} SpeedMBps={limitSpeed:F1} LimiterBypassed={!isLimiterEnabled} AppBufferSize={currentBufferSize} FileStreamBufferSize={FileStreamInternalBufferSize} SequentialScan=True ArtificialDelay=False");

                long fileCopyStartTime = Stopwatch.GetTimestamp();
                double lastWriteCompMs = 0;
                double maxNoProgressMs = 0;

                int writesOver250ms = 0;
                int writesOver500ms = 0;
                int writesOver1000ms = 0;

                long lastSampleTimestamp = Stopwatch.GetTimestamp();
                long lastSampleBytes = 0;
                int readOps = 0;
                double totalReadMs = 0;
                double maxReadMs = 0;
                int writeOps = 0;
                double totalWriteMs = 0;
                double maxWriteMs = 0;

                var sourceOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = FileStreamInternalBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                var destOptions = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = FileStreamInternalBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                using (var sourceStream = new FileStream(sourcePath, sourceOptions))
                using (var destStream = new FileStream(destPath, destOptions))
                {
                    int bytesRead;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (pauseToken != null && pauseToken.IsPaused)
                        {
                            progressThrottler?.ReportState(ExecutionState.Paused, "Duraklatıldı");
                            await pauseToken.WaitWhilePausedAsync(cancellationToken);
                            progressThrottler?.ReportState(ExecutionState.Running, null);
                        }

                        long r0 = Stopwatch.GetTimestamp();
                        bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, currentBufferSize), cancellationToken);
                        long r1 = Stopwatch.GetTimestamp();
                        double readMs = (r1 - r0) * 1000.0 / Stopwatch.Frequency;
                        readOps++;
                        totalReadMs += readMs;
                        if (readMs > maxReadMs) maxReadMs = readMs;

                        if (readMs >= 250)
                        {
                            _logService?.LogWarning($"[SLOW IO] Operation=READ CurrentFileBytesBefore={currentFileBytesCopied} Bytes={bytesRead} DurationMs={readMs:N1} Source={Path.GetFileName(sourcePath)}");
                        }

                        if (bytesRead <= 0) break;

                        cancellationToken.ThrowIfCancellationRequested();

                        if (pauseToken != null && pauseToken.IsPaused)
                        {
                            progressThrottler?.ReportState(ExecutionState.Paused, "Duraklatıldı");
                            await pauseToken.WaitWhilePausedAsync(cancellationToken);
                            progressThrottler?.ReportState(ExecutionState.Running, null);
                        }

                        if (bandwidthLimiter != null && bandwidthLimiter.IsEnabled)
                        {
                            await bandwidthLimiter.ConsumeAsync(bytesRead, cancellationToken);
                        }

                        long w0 = Stopwatch.GetTimestamp();
                        await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        long w1 = Stopwatch.GetTimestamp();
                        double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                        writeOps++;
                        totalWriteMs += writeMs;
                        if (writeMs > maxWriteMs) maxWriteMs = writeMs;

                        double writeCompMs = (w1 - fileCopyStartTime) * 1000.0 / Stopwatch.Frequency;
                        double gapMs = writeCompMs - lastWriteCompMs;
                        if (gapMs > maxNoProgressMs) maxNoProgressMs = gapMs;
                        lastWriteCompMs = writeCompMs;

                        if (writeMs >= 250)
                        {
                            writesOver250ms++;
                            if (writeMs >= 500) writesOver500ms++;
                            if (writeMs >= 1000) writesOver1000ms++;

                            _logService?.LogWarning($"[SLOW IO] Operation=WRITE CurrentFileBytesBefore={currentFileBytesCopied} Bytes={bytesRead} DurationMs={writeMs:N1} Destination={Path.GetFileName(destPath)}");
                        }

                        currentFileBytesCopied += bytesRead;
                        onChunkWritten?.Invoke(currentFileBytesCopied);

                        // 16 MiB boundary tracking
                        if (currentFileBytesCopied >= next16MiBBoundary)
                        {
                            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                            _logService?.LogInformation($"[16MIB BOUNDARY] BoundaryBytes={next16MiBBoundary} Timestamp='{ts}' LastReadMs={readMs:N1} LastWriteMs={writeMs:N1}");
                            next16MiBBoundary += 16 * 1024 * 1024L;
                        }

                        // Progress reporting and timing
                        long p0 = Stopwatch.GetTimestamp();
                        progressThrottler?.ReportChunk(bytesRead, currentFileBytesCopied, fileSize);
                        long p1 = Stopwatch.GetTimestamp();
                        double progMs = (p1 - p0) * 1000.0 / Stopwatch.Frequency;
                        if (progMs >= 10)
                        {
                            _logService?.LogWarning($"[SLOW PROGRESS REPORT] DurationMs={progMs:N1}");
                        }

                        // 500 ms periodic sample aggregation
                        long nowTs = Stopwatch.GetTimestamp();
                        double elapsedMs = (nowTs - lastSampleTimestamp) * 1000.0 / Stopwatch.Frequency;
                        if (elapsedMs >= 500)
                        {
                            long bytesSinceSample = currentFileBytesCopied - lastSampleBytes;
                            long streamPos = destStream.Position;

                            _logService?.LogInformation($"[COPY IO SAMPLE] ElapsedMs={elapsedMs:N0} CurrentFileBytes={currentFileBytesCopied} BytesSinceSample={bytesSinceSample} ReadOps={readOps} TotalReadMs={totalReadMs:N1} MaxReadMs={maxReadMs:N1} WriteOps={writeOps} TotalWriteMs={totalWriteMs:N1} MaxWriteMs={maxWriteMs:N1}");
                            _logService?.LogInformation($"[DESTINATION GROWTH] AppBytes={currentFileBytesCopied} StreamPosition={streamPos} DeltaSinceSample={bytesSinceSample}");

                            lastSampleTimestamp = nowTs;
                            lastSampleBytes = currentFileBytesCopied;
                            readOps = 0;
                            totalReadMs = 0;
                            maxReadMs = 0;
                            writeOps = 0;
                            totalWriteMs = 0;
                            maxWriteMs = 0;
                        }
                    }
                }

                long fileCopyEndTime = Stopwatch.GetTimestamp();
                double totalCopyMs = (fileCopyEndTime - fileCopyStartTime) * 1000.0 / Stopwatch.Frequency;
                double averageMiBps = totalCopyMs > 0 ? (currentFileBytesCopied / 1048576.0) / (totalCopyMs / 1000.0) : 0;

                _logService?.LogInformation($"[COPY FILE PERF SUMMARY] BufferBytes={currentBufferSize} FileBytes={currentFileBytesCopied} ElapsedMs={totalCopyMs:F1} AverageMiBps={averageMiBps:F2} WriteCount={writeOps} WritesOver250ms={writesOver250ms} WritesOver500ms={writesOver500ms} WritesOver1000ms={writesOver1000ms} MaxWriteMs={maxWriteMs:F1} MaxNoProgressMs={maxNoProgressMs:F1} BandwidthMode={bwMode} LimiterBypassed={!isLimiterEnabled} ArtificialDelay=False");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private bool CheckIsSameFile(FileTaskItem item, bool useHash)
        {
            try
            {
                var destFi = new FileInfo(item.DestinationPath);
                if (!destFi.Exists) return false;
                if (destFi.Length != item.Length) return false;

                TimeSpan diff = (destFi.LastWriteTimeUtc > item.LastWriteTime)
                    ? destFi.LastWriteTimeUtc - item.LastWriteTime
                    : item.LastWriteTime - destFi.LastWriteTimeUtc;

                if (diff.TotalSeconds > 2) return false;

                if (useHash)
                {
                    return CompareFileHashes(item.SourcePath, item.DestinationPath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool CheckFilesMatch(string path1, string path2, bool useHash)
        {
            try
            {
                var fi1 = new FileInfo(path1);
                var fi2 = new FileInfo(path2);
                if (!fi1.Exists || !fi2.Exists) return false;
                if (fi1.Length != fi2.Length) return false;
                if (useHash) return CompareFileHashes(path1, path2);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool CompareFileHashes(string path1, string path2)
        {
            try
            {
                using var sha1 = SHA256.Create();
                using var sha2 = SHA256.Create();

                using var stream1 = new FileStream(path1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var stream2 = new FileStream(path2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                byte[] hash1 = sha1.ComputeHash(stream1);
                byte[] hash2 = sha2.ComputeHash(stream2);

                return hash1.SequenceEqual(hash2);
            }
            catch
            {
                return false;
            }
        }

        public async Task<FileCopyResult> RetryFailedFilesAsync(
            Job job,
            List<FileItemResult> failedItems,
            bool dryRun,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken,
            IPauseToken? pauseToken = null,
            JobCheckpoint? resumeCheckpoint = null,
            ExecutionTriggerSource triggerSource = ExecutionTriggerSource.HistoryRetry)
        {
            var result = new FileCopyResult { Success = true, Status = JobResultStatus.Success };
            if (failedItems == null || !failedItems.Any())
            {
                return result;
            }

            var pendingRetryList = failedItems.Where(f => f.Status == FileItemStatus.Failed || f.Status == FileItemStatus.Retrying || f.Status == FileItemStatus.Incomplete || f.Status == FileItemStatus.Cancelled || f.Status == FileItemStatus.Pending).ToList();
            if (!pendingRetryList.Any())
            {
                return result;
            }

            _logService?.LogInformation($"[HISTORY RETRY EXECUTION] JobId={job.Id} Name='{job.Name}' FilesCount={pendingRetryList.Count} Source={triggerSource}");

            var checkpoint = resumeCheckpoint;
            if (checkpoint == null && _checkpointRepository != null && job.Id != Guid.Empty)
            {
                try { checkpoint = await _checkpointRepository.GetCheckpointAsync(job.Id); } catch { }
            }

            if (checkpoint != null)
            {
                checkpoint.CurrentState = ExecutionState.Running;
                checkpoint.InterruptionReasonCode = ExecutionInterruptionReason.None;
                checkpoint.InterruptionReason = null;
                checkpoint.ProcessInstanceId = JobExecutionGate.CurrentProcessInstanceId;
                checkpoint.UpdatedAt = DateTime.Now;
                await SaveCheckpointSafeAsync(checkpoint);
            }

            int totalFiles = pendingRetryList.Count;
            long totalBytes = pendingRetryList.Sum(f => f.FileSize);
            int filesCopied = 0;
            int filesFailed = 0;
            long bytesCopied = 0;

            var throttler = new ProgressThrottler(progress, job, totalFiles, totalBytes);
            throttler.ReportStart();

            try
            {
                foreach (var item in pendingRetryList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken != null) await pauseToken.WaitWhilePausedAsync(cancellationToken);

                    item.Status = FileItemStatus.Retrying;
                    item.RetryCount++;
                    item.Timestamp = DateTime.Now;

                    int retries = Math.Max(1, job.RetryCount);
                    for (int attempt = 1; attempt <= retries; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pauseToken != null) await pauseToken.WaitWhilePausedAsync(cancellationToken);

                        try
                        {
                            if (!File.Exists(item.SourcePath))
                            {
                                item.ErrorMessage = "Kaynak dosya bulunamadı.";
                                item.Status = FileItemStatus.Failed;
                                filesFailed++;
                                throttler.ReportFileCompleted(filesCopied, 0, filesFailed);
                                result.Errors.Add($"Kaynak bulunamadı: '{item.SourcePath}'");
                                break;
                            }

                            var targetDir = Path.GetDirectoryName(item.DestinationPath);
                            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }

                            string tempPath = item.DestinationPath + ".tmp";
                            if (File.Exists(tempPath))
                            {
                                try { File.Delete(tempPath); } catch { }
                            }

                            await CopyFileStreamAsync(item.SourcePath, tempPath, item.FileSize, null, pauseToken, throttler, cancellationToken);

                            if (File.Exists(item.DestinationPath))
                            {
                                File.Delete(item.DestinationPath);
                            }
                            File.Move(tempPath, item.DestinationPath);

                            item.Status = FileItemStatus.Completed;
                            item.ErrorMessage = null;
                            filesCopied++;
                            bytesCopied += item.FileSize;

                            throttler.ReportFileCompleted(filesCopied, 0, filesFailed);
                            _logService?.LogInformation($"Dosya yeniden denemeyle kopyalandı: {item.FileName}");
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            item.ErrorMessage = UserFriendlyErrorTranslator.Translate(ex);

                            if (IsDriveDisconnectedException(ex) || !IsDriveConnected(item.DestinationPath))
                            {
                                item.Status = FileItemStatus.WaitingForDestination;
                                _logService?.LogWarning("USB sürücüsü çıkarıldı. Yeniden deneme işlemi beklemeye alındı.");

                                if (checkpoint != null)
                                {
                                    checkpoint.CurrentState = ExecutionState.DestinationUnavailable;
                                    checkpoint.DestinationWasUnavailable = true;
                                    checkpoint.InterruptionReason = "Hedef USB sürücüsünün bağlantısı kesildi.";
                                    await SaveCheckpointSafeAsync(checkpoint);
                                }

                                await EnsureDestinationDriveAvailableAsync(item.DestinationPath, progress, cancellationToken, job, throttler);
                                attempt--;
                                continue;
                            }

                            if (attempt == retries)
                            {
                                item.Status = FileItemStatus.Failed;
                                filesFailed++;
                                result.Errors.Add($"'{item.FileName}': {item.ErrorMessage}");
                                _logService?.LogError($"Dosya yeniden denemesi başarısız oldu ('{item.FileName}'): {item.ErrorMessage}");
                            }
                            else
                            {
                                await Task.Delay(job.RetryDelay > TimeSpan.Zero ? job.RetryDelay : TimeSpan.FromSeconds(1), cancellationToken);
                            }
                        }
                    }

                    result.FileResults.Add(item);
                }
            }
            catch (OperationCanceledException cancelEx)
            {
                _logService?.LogInformation($"[HISTORY RETRY CANCELED] JobId={job.Id}");
                result.FilesCopied = filesCopied;
                result.FilesFailed = filesFailed;
                result.BytesCopied = bytesCopied;
                result.Success = false;
                result.Status = JobResultStatus.Cancelled;

                if (checkpoint != null && _checkpointRepository != null)
                {
                    checkpoint.CurrentState = ExecutionState.Cancelled;
                    checkpoint.InterruptionReasonCode = ExecutionInterruptionReason.UserCancelled;
                    checkpoint.InterruptionReason = "Kullanıcı tarafından iptal edildi.";
                    await SaveCheckpointSafeAsync(checkpoint);
                }

                cancelEx.Data["FileCopyResult"] = result;
                throw;
            }

            result.FilesCopied = filesCopied;
            result.FilesFailed = filesFailed;
            result.BytesCopied = bytesCopied;
            result.Success = filesFailed == 0;
            result.Status = filesFailed == 0 ? JobResultStatus.Success : (filesCopied > 0 ? JobResultStatus.PartialSuccess : JobResultStatus.Failure);

            throttler.ReportFinal(result.Success, result.Success ? "Yeniden deneme tamamlandı." : "Yeniden deneme bazı hatalarla tamamlandı.");

            if (!dryRun && _checkpointRepository != null)
            {
                if (result.Success)
                {
                    await _checkpointRepository.DeleteCheckpointAsync(job.Id);
                }
                else if (checkpoint != null)
                {
                    checkpoint.CurrentState = ExecutionState.Failed;
                    checkpoint.IsRecoverable = true;
                    await SaveCheckpointSafeAsync(checkpoint);
                }
            }

            return result;
        }

        private async Task SaveCheckpointSafeAsync(JobCheckpoint? checkpoint)
        {
            if (checkpoint == null || _checkpointRepository == null) return;
            var sw = Stopwatch.StartNew();
            try
            {
                await _checkpointRepository.SaveCheckpointAsync(checkpoint);
                sw.Stop();
                _logService?.LogInformation($"[CHECKPOINT SAVE] CompletedBytes={checkpoint.CompletedBytes} State={checkpoint.CurrentState} DurationMs={sw.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logService?.LogWarning($"[CHECKPOINT SAVE FAIL] DurationMs={sw.ElapsedMilliseconds} Err={ex.Message}");
            }
        }

        private bool ValidateCompletedFile(FileTaskItem item, CheckpointFileEntry entry, VerificationMode mode, bool useHash)
        {
            // Source change detection (Requirement 8)
            if (entry.SourceLength != item.Length)
            {
                _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Source length changed: '{item.SourcePath}' (entry: {entry.SourceLength}, current: {item.Length}). Invalidation = true.");
                return false;
            }
            TimeSpan sourceDiff = (entry.SourceLastWriteTimeUtc > item.LastWriteTime)
                ? entry.SourceLastWriteTimeUtc - item.LastWriteTime
                : item.LastWriteTime - entry.SourceLastWriteTimeUtc;
            if (sourceDiff.TotalSeconds > 2)
            {
                _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Source last write time changed: '{item.SourcePath}' (entry: {entry.SourceLastWriteTimeUtc}, current: {item.LastWriteTime}). Invalidation = true.");
                return false;
            }

            // Destination validation (Requirement 7)
            if (!File.Exists(item.DestinationPath))
            {
                _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Destination file missing: '{item.DestinationPath}'. Invalidation = true.");
                return false;
            }

            if (mode == VerificationMode.SizeAndTimestamp)
            {
                try
                {
                    var destFi = new FileInfo(item.DestinationPath);
                    if (destFi.Length != item.Length)
                    {
                        _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Destination size mismatch: '{item.DestinationPath}' (expected: {item.Length}, dest: {destFi.Length}). Invalidation = true.");
                        return false;
                    }
                    TimeSpan destDiff = (destFi.LastWriteTimeUtc > item.LastWriteTime)
                        ? destFi.LastWriteTimeUtc - item.LastWriteTime
                        : item.LastWriteTime - destFi.LastWriteTimeUtc;
                    if (destDiff.TotalSeconds > 2)
                    {
                        _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Destination last write time mismatch: '{item.DestinationPath}' (source: {item.LastWriteTime}, dest: {destFi.LastWriteTimeUtc}, diff: {destDiff.TotalSeconds}s). Invalidation = true.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logService?.LogInformation($"[RECOVERY FILE VALIDATION] Exception inspecting destination file '{item.DestinationPath}': {ex.Message}. Invalidation = true.");
                    return false;
                }
            }
            else if (mode == VerificationMode.SHA256 || useHash)
            {
                if (!CompareFileHashes(item.SourcePath, item.DestinationPath))
                {
                    _logService?.LogInformation($"[RECOVERY FILE VALIDATION] SHA256 mismatch between source and destination for '{item.SourcePath}'. Invalidation = true.");
                    return false;
                }
            }

            _logService?.LogInformation($"[RECOVERY FILE VALIDATION] File validated successfully: '{item.SourcePath}'. Will skip recopying.");
            return true;
        }

        private string GetUniqueFilePath(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int count = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{fileName} ({count}){ext}");
                count++;
            } while (File.Exists(candidate));

            return candidate;
        }

        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }

        private class FileTaskItem
        {
            public string SourcePath { get; set; } = string.Empty;
            public string DestinationPath { get; set; } = string.Empty;
            public long Length { get; set; }
            public DateTime LastWriteTime { get; set; }
        }
    }
}
