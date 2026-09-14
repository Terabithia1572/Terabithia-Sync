using System;
using System.IO;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public static class UserFriendlyErrorTranslator
    {
        public static string Translate(Exception ex)
        {
            if (ex == null) return "Bilinmeyen hata.";

            string msg = ex.Message;

            if (ex is IOException ioEx)
            {
                int hResult = ioEx.HResult & 0xFFFF;
                if (hResult == 32 || hResult == 33 || msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                {
                    return "Dosya başka bir program veya kullanıcı tarafından kullanılıyor.";
                }
                if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    return "Hedefte aynı isimde dosya zaten mevcut.";
                }
                if (msg.Contains("not enough space", StringComparison.OrdinalIgnoreCase) || hResult == 112)
                {
                    return "Hedef sürücüde yeterli boş alan yok.";
                }
            }

            if (ex is UnauthorizedAccessException || msg.Contains("Access to the path", StringComparison.OrdinalIgnoreCase) || msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                return "Dosyaya veya hedefe erişim engellendi. Yetki yetersiz olabilir.";
            }

            if (ex is FileNotFoundException || msg.Contains("Could not find file", StringComparison.OrdinalIgnoreCase))
            {
                return "Kaynak dosya bulunamadı veya taşınmış.";
            }

            if (ex is DirectoryNotFoundException || msg.Contains("Could not find a part of the path", StringComparison.OrdinalIgnoreCase))
            {
                return "Klasör yolu bulunamadı veya hedef sürücü bağlı değil.";
            }

            if (ex is PathTooLongException || msg.Contains("path is too long", StringComparison.OrdinalIgnoreCase))
            {
                return "Dosya yolu Windows karakter sınırını aşıyor.";
            }

            return $"İşlem hatası: {msg}";
        }
    }
}
