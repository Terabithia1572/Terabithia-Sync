using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class PathValidationService : IPathValidationService
    {
        public Task<IReadOnlyList<string>> ValidateSourcesAsync(IEnumerable<string> sourcePaths)
        {
            var errors = new List<string>();
            if (sourcePaths == null || !sourcePaths.Any())
            {
                errors.Add("En az bir kaynak dosya veya klasör seçilmelidir.");
                return Task.FromResult<IReadOnlyList<string>>(errors);
            }

            foreach (var src in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(src))
                {
                    errors.Add("Kaynak yol boş olamaz.");
                    continue;
                }

                if (ContainsInvalidPathChars(src))
                {
                    errors.Add($"Geçersiz karakter içeren kaynak yol: '{src}'");
                    continue;
                }

                try
                {
                    string fullPath = Path.GetFullPath(src);
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        errors.Add($"Kaynak yol sistemde bulunamadı: '{src}'");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Kaynak yol doğrulanamadı '{src}': {ex.Message}");
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        public Task<IReadOnlyList<string>> ValidateDestinationAsync(string destinationPath)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                errors.Add("Hedef klasör yolu boş olamaz.");
                return Task.FromResult<IReadOnlyList<string>>(errors);
            }

            if (ContainsInvalidPathChars(destinationPath))
            {
                errors.Add($"Geçersiz karakter içeren hedef yol: '{destinationPath}'");
                return Task.FromResult<IReadOnlyList<string>>(errors);
            }

            try
            {
                string fullPath = Path.GetFullPath(destinationPath);
                if (File.Exists(fullPath))
                {
                    errors.Add("Hedef yol mevcut bir dosyayı gösteriyor. Hedef bir klasör olmalıdır.");
                }
                else
                {
                    string? root = Path.GetPathRoot(fullPath);
                    if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                    {
                        errors.Add($"Hedef sürücü veya ağ konumu erişilebilir değil: '{root}'");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Hedef yol doğrulanamadı '{destinationPath}': {ex.Message}");
            }

            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        public Task<IReadOnlyList<string>> ValidateJobPathsAsync(IEnumerable<string> sourcePaths, string destinationPath)
        {
            var errors = new List<string>();

            var srcErrors = ValidateSourcesAsync(sourcePaths).Result;
            errors.AddRange(srcErrors);

            var destErrors = ValidateDestinationAsync(destinationPath).Result;
            errors.AddRange(destErrors);

            if (errors.Any())
                return Task.FromResult<IReadOnlyList<string>>(errors);

            try
            {
                string fullDest = Path.GetFullPath(destinationPath).TrimEnd('\\', '/');

                foreach (var src in sourcePaths)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    string fullSrc = Path.GetFullPath(src).TrimEnd('\\', '/');

                    if (string.Equals(fullSrc, fullDest, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Kaynak ve hedef klasör aynı olamaz: '{src}'");
                    }

                    if (Directory.Exists(fullSrc))
                    {
                        string folderName = Path.GetFileName(fullSrc);
                        string targetSubDir = Path.Combine(fullDest, folderName).TrimEnd('\\', '/');

                        if (string.Equals(fullSrc, targetSubDir, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Kaynak ve hedef alt klasör aynı konuma karşılık geliyor: '{src}'");
                        }

                        if (fullDest.StartsWith(fullSrc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Hedef klasör, kaynak klasörün alt klasörü olamaz: '{src}' -> '{destinationPath}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Yollar karşılaştırılırken hata oluştu: {ex.Message}");
            }

            return Task.FromResult<IReadOnlyList<string>>(errors);
        }

        private bool ContainsInvalidPathChars(string path)
        {
            char[] invalidChars = Path.GetInvalidPathChars();
            return path.Any(c => invalidChars.Contains(c));
        }
    }
}
