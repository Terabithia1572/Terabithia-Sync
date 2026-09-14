using System;
using System.IO;
using System.Threading.Tasks;
using Quartz;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

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

        public QuartzCopyJob(
            IJobRepository jobRepository,
            IFileCopyService fileCopyService,
            IHistoryRepository historyRepository,
            INotificationService notificationService,
            IUsbDriveService usbDriveService,
            ILogService logService)
        {
            _jobRepository = jobRepository;
            _fileCopyService = fileCopyService;
            _historyRepository = historyRepository;
            _notificationService = notificationService;
            _usbDriveService = usbDriveService;
            _logService = logService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var dataMap = context.MergedJobDataMap;
            string? jobIdStr = dataMap.GetString("JobId");
            bool dryRun = dataMap.GetBoolean("DryRun");

            if (!Guid.TryParse(jobIdStr, out Guid jobId))
            {
                _logService.LogError($"QuartzCopyJob geçersiz GörevKimliği ile çağrıldı: '{jobIdStr}'");
                return;
            }

            var job = await _jobRepository.GetByIdAsync(jobId);
            if (job == null)
            {
                _logService.LogWarning($"Zamanlanmış görev veritabanında bulunamadı: {jobId}");
                return;
            }

            if (!job.Enabled && !dataMap.GetBoolean("ManualTrigger"))
            {
                _logService.LogInformation($"Devre dışı bırakılan '{job.Name}' görevi atlandı.");
                return;
            }

            // Check USB drive connection if job specifies USB destination
            if (job.IsUsbDestination && !_usbDriveService.IsDriveConnected(job.DestinationPath))
            {
                _logService.LogWarning($"'{job.Name}' kopyalama görevi çalıştırılamadı. Hedef USB sürücüsü ('{job.DestinationPath}') bağlı değil.");

                var usbHistoryEntry = new HistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                    Status = JobResultStatus.Skipped,
                    Message = "Hedef USB sürücüsü bağlı olmadığı için işlem atlandı."
                };

                await _historyRepository.AddAsync(usbHistoryEntry);
                _notificationService.ShowNotification("Terabithia Sync - USB Sürücü Bağlı Değil", $"'{job.Name}' görevi için hedef USB sürücüsü bulunamadı.", NotificationType.Warning);
                return;
            }

            _logService.LogInformation($"'{job.Name}' kopyalama görevi başlatılıyor (Simülasyon: {dryRun})");
            DateTime startTime = DateTime.Now;

            try
            {
                var result = await _fileCopyService.CopyAsync(job, dryRun, null, context.CancellationToken);
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

                _logService.LogInformation($"'{job.Name}' görevi tamamlandı. Kopyalanan: {result.FilesCopied}, Atlanan: {result.FilesSkipped}, Hatalı: {result.FilesFailed}, Veri: {result.BytesCopied / 1024} KB");

                _notificationService.ShowJobResultNotification(
                    job.Name,
                    result.Success,
                    result.FilesCopied,
                    result.FilesFailed,
                    historyEntry.Message);
            }
            catch (Exception ex)
            {
                DateTime endTime = DateTime.Now;
                _logService.LogError($"'{job.Name}' görevi çalıştırılırken beklenmeyen hata", ex);

                var historyEntry = new HistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    StartTime = startTime,
                    EndTime = endTime,
                    Status = JobResultStatus.Failure,
                    Message = $"Kritik Hata: {ex.Message}",
                    Errors = new System.Collections.Generic.List<string> { ex.ToString() }
                };

                await _historyRepository.AddAsync(historyEntry);

                job.LastRun = startTime;
                job.LastResult = JobResultStatus.Failure;
                await _jobRepository.UpdateAsync(job);

                _notificationService.ShowJobResultNotification(job.Name, false, 0, 1, ex.Message);
            }
        }
    }
}
