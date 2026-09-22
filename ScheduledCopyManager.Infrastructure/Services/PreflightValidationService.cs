using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class PreflightValidationService : IPreflightValidationService
    {
        private readonly IPathValidationService _pathValidationService;

        public PreflightValidationService(IPathValidationService pathValidationService)
        {
            _pathValidationService = pathValidationService ?? throw new ArgumentNullException(nameof(pathValidationService));
        }

        public Task<PreflightResult> ValidateJobAsync(Job job)
        {
            var result = new PreflightResult();

            if (job == null)
            {
                result.AddIssue(new PreflightIssue
                {
                    Code = PreflightIssueCode.GENERAL_ERROR,
                    Severity = PreflightSeverity.BlockingError,
                    Title = "Görev Bulunamadı",
                    Message = "Doğrulanacak görev nesnesi geçersiz (null).",
                    SuggestedAction = "Lütfen görevi tekrar seçin."
                });
                return Task.FromResult(result);
            }

            if (job.SourcePaths == null || job.SourcePaths.Count == 0)
            {
                result.AddIssue(new PreflightIssue
                {
                    Code = PreflightIssueCode.SOURCE_NOT_FOUND,
                    Severity = PreflightSeverity.BlockingError,
                    Title = "Kaynak Yol Seçilmedi",
                    Message = "En az bir kaynak dosya veya klasör seçilmelidir.",
                    SuggestedAction = "Görev ayarlarından bir kaynak yol ekleyin."
                });
                return Task.FromResult(result);
            }

            if (string.IsNullOrWhiteSpace(job.DestinationPath))
            {
                result.AddIssue(new PreflightIssue
                {
                    Code = PreflightIssueCode.DESTINATION_NOT_FOUND,
                    Severity = PreflightSeverity.BlockingError,
                    Title = "Hedef Yol Boş",
                    Message = "Hedef klasör yolu belirtilmemiş.",
                    SuggestedAction = "Görev ayarlarında hedef klasör yolunu tanımlayın."
                });
                return Task.FromResult(result);
            }

            // 1. Path Safety & Relationships
            string normalizedDest = NormalizePath(job.DestinationPath);
            string? destRoot = Path.GetPathRoot(normalizedDest);

            // Check destination drive accessibility
            if (!string.IsNullOrEmpty(destRoot))
            {
                if (!Directory.Exists(destRoot))
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.DESTINATION_UNAVAILABLE,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Hedef Sürücü Erişilemez",
                        Message = $"Hedef sürücüye veya ağ konumuna erişilemiyor: '{destRoot}'",
                        Path = job.DestinationPath,
                        SuggestedAction = "Sürücünün veya ağ bağlantısının aktif olduğunu kontrol edin."
                    });
                }
            }

            // Check source existence & access
            long totalSourceSizeBytes = 0;

            foreach (var rawSrc in job.SourcePaths)
            {
                if (string.IsNullOrWhiteSpace(rawSrc)) continue;
                string normalizedSrc = NormalizePath(rawSrc);

                bool isFile = File.Exists(normalizedSrc);
                bool isDir = Directory.Exists(normalizedSrc);

                if (!isFile && !isDir)
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.SOURCE_NOT_FOUND,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Kaynak Bulunamadı",
                        Message = $"Belirtilen kaynak yol sistemde mevcut değil: '{rawSrc}'",
                        Path = rawSrc,
                        SuggestedAction = "Kaynak yolun taşınmadığından veya silinmediğinden emin olun."
                    });
                    continue;
                }

                // Check source readability
                try
                {
                    if (isFile)
                    {
                        using var fs = new FileStream(normalizedSrc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        totalSourceSizeBytes += fs.Length;
                    }
                    else if (isDir)
                    {
                        var dirInfo = new DirectoryInfo(normalizedSrc);
                        // Access test by enumerating first item
                        dirInfo.EnumerateFileSystemInfos().FirstOrDefault();

                        try
                        {
                            var files = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories);
                            foreach (var file in files)
                            {
                                totalSourceSizeBytes += file.Length;
                            }
                        }
                        catch
                        {
                            // Partial scan ok
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.SOURCE_ACCESS_DENIED,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Kaynağa Erişim Reddedildi",
                        Message = $"Kaynak yola okuma erişimi engellendi: '{rawSrc}'",
                        Path = rawSrc,
                        SuggestedAction = "Gerekli dosya/klasör izinlerini kontrol edin."
                    });
                }
                catch (Exception ex)
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.SOURCE_ACCESS_DENIED,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Kaynak Okuma Hatası",
                        Message = $"Kaynak yol okunurken hata oluştu: '{rawSrc}' ({ex.Message})",
                        Path = rawSrc
                    });
                }

                // Path comparison safety
                if (string.Equals(normalizedSrc, normalizedDest, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.SOURCE_EQUALS_DESTINATION,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Kaynak ve Hedef Aynı",
                        Message = $"Kaynak yol ve hedef yol tamamen aynı olamaz: '{rawSrc}'",
                        Path = rawSrc,
                        SuggestedAction = "Farklı bir hedef klasör seçin."
                    });
                }

                // Check if destination is inside source (recursive copy loop)
                string srcWithSep = normalizedSrc.EndsWith(Path.DirectorySeparatorChar.ToString()) 
                    ? normalizedSrc 
                    : normalizedSrc + Path.DirectorySeparatorChar;

                if (normalizedDest.StartsWith(srcWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.DESTINATION_INSIDE_SOURCE,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Tehlikeli Özyinelemeli Yol",
                        Message = $"Hedef klasör, kaynak klasörün alt klasörü olarak yer alıyor. Bu durum sonsuz kopyalama döngüsüne yol açabilir: '{normalizedDest}' -> '{normalizedSrc}'",
                        Path = rawSrc,
                        SuggestedAction = "Hedef klasörü kaynak klasörün dışına taşıyın."
                    });
                }

                // Check if source is inside destination
                string destWithSep = normalizedDest.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? normalizedDest
                    : normalizedDest + Path.DirectorySeparatorChar;

                if (normalizedSrc.StartsWith(destWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.SOURCE_INSIDE_DESTINATION,
                        Severity = PreflightSeverity.Warning,
                        Title = "Kaynak Hedef İçinde",
                        Message = $"Kaynak klasör, hedef klasörün alt klasörü durumunda: '{normalizedSrc}'",
                        Path = rawSrc,
                        SuggestedAction = "Çakışmaları ve yinelenen kopyalamaları önlemek için yolları kontrol edin."
                    });
                }
            }

            // 2. Destination Writability
            if (!result.HasBlockingErrors)
            {
                try
                {
                    if (!Directory.Exists(normalizedDest))
                    {
                        Directory.CreateDirectory(normalizedDest);
                    }

                    // Test write permission by creating a temporary file
                    string testFile = Path.Combine(normalizedDest, $".terabithia_preflight_{Guid.NewGuid():N}.tmp");
                    using (var fs = File.Create(testFile, 1, FileOptions.DeleteOnClose))
                    {
                        // Writable check passed
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.DESTINATION_NOT_WRITABLE,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Hedef Klasöre Yazılamıyor",
                        Message = $"Hedef klasöre yazma erişimi engellendi veya salt okunur: '{job.DestinationPath}'",
                        Path = job.DestinationPath,
                        SuggestedAction = "Hedef klasörün yazma izinlerini kontrol edin."
                    });
                }
                catch (Exception ex)
                {
                    result.AddIssue(new PreflightIssue
                    {
                        Code = PreflightIssueCode.DESTINATION_NOT_WRITABLE,
                        Severity = PreflightSeverity.BlockingError,
                        Title = "Hedef Klasör Oluşturma/Yazma Hatası",
                        Message = $"Hedef klasör hazırlığı sırasında hata oluştu: '{job.DestinationPath}' ({ex.Message})",
                        Path = job.DestinationPath
                    });
                }
            }

            // 3. Disk Space Validation
            if (!result.HasBlockingErrors && !string.IsNullOrEmpty(destRoot))
            {
                try
                {
                    var drive = new DriveInfo(destRoot);
                    if (drive.IsReady)
                    {
                        long freeBytes = drive.AvailableFreeSpace;
                        bool isIncremental = job.CopyMode == CopyMode.Incremental;
                        
                        if (totalSourceSizeBytes > 0)
                        {
                            if (freeBytes < totalSourceSizeBytes)
                            {
                                string reqFormatted = FormatBytes(totalSourceSizeBytes);
                                string freeFormatted = FormatBytes(freeBytes);

                                if (isIncremental)
                                {
                                    result.AddIssue(new PreflightIssue
                                    {
                                        Code = PreflightIssueCode.INSUFFICIENT_SPACE,
                                        Severity = PreflightSeverity.Warning,
                                        Title = "Yetersiz Disk Alanı Uyarısı",
                                        Message = $"Hedef sürücüdeki boş alan ({freeFormatted}), toplam kaynak boyutundan ({reqFormatted}) az. Artımlı (Incremental) mod kullanıldığı için kopyalama tamamlanabilir ancak disk dolabilir.",
                                        Path = job.DestinationPath,
                                        SuggestedAction = "Hedef sürücüde yer açın veya yer durumunu izleyin."
                                    });
                                }
                                else
                                {
                                    result.AddIssue(new PreflightIssue
                                    {
                                        Code = PreflightIssueCode.INSUFFICIENT_SPACE,
                                        Severity = PreflightSeverity.BlockingError,
                                        Title = "Yetersiz Disk Alanı",
                                        Message = $"Hedef sürücüde yeterli alan yok. Gerekli tahmini alan: {reqFormatted}, Mevcut boş alan: {freeFormatted}.",
                                        Path = job.DestinationPath,
                                        SuggestedAction = "Hedef sürücüde yer açın veya farklı bir hedef seçin."
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // UNC path or drive info not available
                }
            }

            return Task.FromResult(result);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                string full = Path.GetFullPath(path);
                return full.TrimEnd('\\', '/');
            }
            catch
            {
                return path.TrimEnd('\\', '/');
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {suffixes[order]}";
        }
    }
}
