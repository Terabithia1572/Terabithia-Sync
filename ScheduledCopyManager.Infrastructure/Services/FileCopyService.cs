using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class FileCopyService : IFileCopyService
    {
        private const int BufferSize = 81920; // 80 KB streaming buffer
        private readonly ILogService? _logService;
        private readonly IUsbDriveService? _usbDriveService;

        public FileCopyService(ILogService? logService = null, IUsbDriveService? usbDriveService = null)
        {
            _logService = logService;
            _usbDriveService = usbDriveService;
        }

        public async Task<FileCopyResult> CopyAsync(
            Job job,
            bool dryRun,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken)
        {
            var result = new FileCopyResult { Success = true, Status = JobResultStatus.Success };
            var fileList = new List<FileTaskItem>();
            var sourceRootMappings = new List<(string SourceRoot, string DestSubDir)>();
            bool hasFatalDiscoveryError = false;

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

            string fullDest = Path.GetFullPath(job.DestinationPath);
            _logService?.LogInformation($"'{(string.IsNullOrWhiteSpace(job.Name) ? "Kopyalama Görevi" : job.Name)}' görevi kopyalama işlemi başlatılıyor.");
            _logService?.LogInformation($"Hedef hazırlanıyor: {fullDest}");

            // Ensure destination drive is available before starting, or wait
            await EnsureDestinationDriveAvailableAsync(fullDest, progress, cancellationToken);

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

            // Step 3: Process files with USB resilience and temporary file streaming
            for (int i = 0; i < fileList.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = fileList[i];

                var fileResultItem = new FileItemResult
                {
                    SourcePath = item.SourcePath,
                    DestinationPath = item.DestinationPath,
                    RelativePath = Path.GetRelativePath(fullDest, item.DestinationPath),
                    FileName = Path.GetFileName(item.SourcePath),
                    FileSize = item.Length,
                    Status = FileItemStatus.Pending,
                    Timestamp = DateTime.Now
                };

                progress?.Report(new FileCopyProgress
                {
                    JobName = job.Name,
                    CurrentFileName = Path.GetFileName(item.SourcePath),
                    FilesCopied = filesCopied,
                    FilesSkipped = filesSkipped,
                    FilesFailed = filesFailed,
                    FilesPending = totalFiles - (filesCopied + filesSkipped + filesFailed),
                    TotalFiles = totalFiles,
                    BytesCopied = currentBytesCopied,
                    TotalBytes = totalBytes,
                    StatusMessage = $"Kopyalanıyor: {Path.GetFileName(item.SourcePath)}"
                });

                // Ensure drive is connected before each file
                await EnsureDestinationDriveAvailableAsync(item.DestinationPath, progress, cancellationToken);

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
                        result.FileResults.Add(fileResultItem);
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
                            result.FileResults.Add(fileResultItem);
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
                    result.FileResults.Add(fileResultItem);
                    _logService?.LogInformation($"[Ön İzleme] Dosya kopyalandı sayıldı: {Path.GetFileName(item.SourcePath)}");
                    continue;
                }

                // Execute file copy with USB interruption retry / resume logic
                int retries = Math.Max(1, job.RetryCount);

                for (int attempt = 1; attempt <= retries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

                        await CopyFileStreamAsync(item.SourcePath, tempPath, cancellationToken);

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

                        filesCopied++;
                        currentBytesCopied += item.Length;
                        fileResultItem.Status = FileItemStatus.Completed;
                        fileResultItem.ErrorMessage = null;
                        _logService?.LogInformation($"Dosya başarıyla kopyalandı: {Path.GetFileName(item.SourcePath)}");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        fileResultItem.Status = FileItemStatus.Cancelled;
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
                            await EnsureDestinationDriveAvailableAsync(targetPath, progress, cancellationToken);
                            _logService?.LogInformation("Bekleyen dosyalar yeniden kopyalanıyor.");
                            attempt--; // Reset attempt to retry this exact file after drive returns
                            continue;
                        }

                        if (attempt == retries)
                        {
                            filesFailed++;
                            fileResultItem.Status = FileItemStatus.Failed;
                            result.Errors.Add($"'{Path.GetFileName(item.SourcePath)}': {friendlyErr}");
                            _logService?.LogError($"Dosya kopyalanamadı ('{Path.GetFileName(item.SourcePath)}'): {friendlyErr}");
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

            progress?.Report(new FileCopyProgress
            {
                FilesCopied = filesCopied,
                TotalFiles = totalFiles,
                BytesCopied = currentBytesCopied,
                TotalBytes = totalBytes,
                StatusMessage = "Kopyalama işlemi tamamlandı."
            });

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

        private async Task EnsureDestinationDriveAvailableAsync(string path, IProgress<FileCopyProgress>? progress, CancellationToken cancellationToken)
        {
            if (IsDriveConnected(path)) return;

            _logService?.LogWarning("USB sürücüsü çıkarıldı. Kopyalama işlemi beklemeye alındı.");

            int[] backoffDelaysMs = new int[] { 2000, 3000, 5000, 10000, 15000 };
            int backoffIndex = 0;

            while (!IsDriveConnected(path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new FileCopyProgress
                {
                    StatusMessage = "USB sürücüsü çıkarıldı. Kopyalama beklemeye alındı. Sürücü bekleniyor..."
                });

                int delay = backoffDelaysMs[Math.Min(backoffIndex, backoffDelaysMs.Length - 1)];
                backoffIndex++;

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
            }

            _logService?.LogInformation("USB sürücüsü yeniden algılandı. Kopyalama işlemine devam ediliyor.");
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

        private async Task CopyFileStreamAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
            using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await sourceStream.CopyToAsync(destStream, BufferSize, cancellationToken);
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
            CancellationToken cancellationToken)
        {
            var result = new FileCopyResult { Success = true, Status = JobResultStatus.Success };
            if (failedItems == null || !failedItems.Any())
            {
                return result;
            }

            var pendingRetryList = failedItems.Where(f => f.Status == FileItemStatus.Failed || f.Status == FileItemStatus.Retrying || f.Status == FileItemStatus.Pending).ToList();
            if (!pendingRetryList.Any())
            {
                return result;
            }

            _logService?.LogInformation($"'{(string.IsNullOrWhiteSpace(job.Name) ? "Görevi" : job.Name)}' için {pendingRetryList.Count} başarısız dosya yeniden deneniyor.");

            int totalFiles = pendingRetryList.Count;
            int filesCopied = 0;
            int filesFailed = 0;
            long bytesCopied = 0;

            foreach (var item in pendingRetryList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.Status = FileItemStatus.Retrying;
                item.RetryCount++;
                item.Timestamp = DateTime.Now;

                progress?.Report(new FileCopyProgress
                {
                    JobName = job.Name,
                    CurrentFileName = item.FileName,
                    FilesCopied = filesCopied,
                    FilesFailed = filesFailed,
                    TotalFiles = totalFiles,
                    BytesCopied = bytesCopied,
                    StatusMessage = $"Yeniden deneniyor: {item.FileName}"
                });

                int retries = Math.Max(1, job.RetryCount);
                bool itemSuccess = false;

                for (int attempt = 1; attempt <= retries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(item.SourcePath))
                        {
                            item.ErrorMessage = "Kaynak dosya bulunamadı.";
                            item.Status = FileItemStatus.Failed;
                            filesFailed++;
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

                        await CopyFileStreamAsync(item.SourcePath, tempPath, cancellationToken);

                        if (File.Exists(item.DestinationPath))
                        {
                            File.Delete(item.DestinationPath);
                        }
                        File.Move(tempPath, item.DestinationPath);

                        item.Status = FileItemStatus.Completed;
                        item.ErrorMessage = null;
                        filesCopied++;
                        bytesCopied += item.FileSize;
                        itemSuccess = true;
                        _logService?.LogInformation($"Dosya yeniden denemeyle kopyalandı: {item.FileName}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.ErrorMessage = UserFriendlyErrorTranslator.Translate(ex);
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

            result.FilesCopied = filesCopied;
            result.FilesFailed = filesFailed;
            result.BytesCopied = bytesCopied;
            result.Success = filesFailed == 0;
            result.Status = filesFailed == 0 ? JobResultStatus.Success : (filesCopied > 0 ? JobResultStatus.PartialSuccess : JobResultStatus.Failure);

            return result;
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
