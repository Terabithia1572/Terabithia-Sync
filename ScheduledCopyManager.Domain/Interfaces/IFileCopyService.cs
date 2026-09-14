using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public class FileCopyProgress
    {
        public string JobName { get; set; } = string.Empty;
        public string CurrentFileName { get; set; } = string.Empty;
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesFailed { get; set; }
        public int FilesPending { get; set; }
        public int TotalFiles { get; set; }
        public long BytesCopied { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan RemainingTime { get; set; } = TimeSpan.Zero;
        public double Percentage => TotalBytes > 0 ? (double)BytesCopied / TotalBytes * 100.0 : (TotalFiles > 0 ? (double)(FilesCopied + FilesSkipped + FilesFailed) / TotalFiles * 100.0 : 0);
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsWaitingForUsb { get; set; }
    }

    public class FileCopyResult
    {
        public bool Success { get; set; }
        public JobResultStatus Status { get; set; } = JobResultStatus.Success;
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesFailed { get; set; }
        public long BytesCopied { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<FileItemResult> FileResults { get; set; } = new();
    }

    public interface IFileCopyService
    {
        Task<FileCopyResult> CopyAsync(
            Job job,
            bool dryRun,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken);

        Task<FileCopyResult> RetryFailedFilesAsync(
            Job job,
            List<FileItemResult> failedItems,
            bool dryRun,
            IProgress<FileCopyProgress>? progress,
            CancellationToken cancellationToken);

        Task<FileCopyResult> CopyAsync(
            IEnumerable<string> sourcePaths,
            string destinationPath,
            CopyMode copyMode,
            ConflictPolicy conflictPolicy,
            bool dryRun,
            IProgress<double>? progress,
            CancellationToken cancellationToken);
    }
}
