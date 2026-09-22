using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ScheduledCopyManager.Domain.Interfaces;
using WpfApp = System.Windows.Application;

namespace ScheduledCopyManager.Presentation.Services
{
    public class LocalizationService : ILocalizationService
    {
        private string _currentLanguage = "tr-TR";
        private readonly Dictionary<string, string> _fallbackStrings = new(StringComparer.OrdinalIgnoreCase);

        public string CurrentLanguage => _currentLanguage;

        public event EventHandler? LanguageChanged;

        public LocalizationService()
        {
            InitializeFallbackStrings();
        }

        public void SetLanguage(string cultureCode)
        {
            string normalized = NormalizeCulture(cultureCode);
            _currentLanguage = normalized;

            if (WpfApp.Current != null)
            {
                try
                {
                    var dictUri = new Uri($"/ScheduledCopyManager.Presentation;component/Resources/Strings.{normalized}.xaml", UriKind.Relative);
                    var newDict = new ResourceDictionary { Source = dictUri };

                    var merged = WpfApp.Current.Resources.MergedDictionaries;
                    var existing = merged.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("/Resources/Strings."));
                    if (existing != null)
                    {
                        merged.Remove(existing);
                    }
                    merged.Add(newDict);
                }
                catch
                {
                    // Fallback to internal dictionary if ResourceDictionary load fails
                }
            }

            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetString(string key, string? defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return defaultValue ?? string.Empty;

            if (WpfApp.Current != null && WpfApp.Current.Resources.Contains(key))
            {
                var value = WpfApp.Current.Resources[key];
                if (value is string strValue) return strValue;
            }

            if (_fallbackStrings.TryGetValue($"{_currentLanguage}:{key}", out var localized))
            {
                return localized;
            }

            if (_fallbackStrings.TryGetValue($"tr-TR:{key}", out var turkishFallback))
            {
                return turkishFallback;
            }

            return defaultValue ?? key;
        }

        private string NormalizeCulture(string cultureCode)
        {
            if (string.IsNullOrWhiteSpace(cultureCode)) return "tr-TR";
            if (cultureCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
            if (cultureCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)) return "tr-TR";
            return "tr-TR"; // Safe default fallback
        }

        private void InitializeFallbackStrings()
        {
            _fallbackStrings["tr-TR:AppTitle"] = "Terabithia Sync";
            _fallbackStrings["en-US:AppTitle"] = "Terabithia Sync";

            _fallbackStrings["tr-TR:Pause"] = "Duraklat";
            _fallbackStrings["en-US:Pause"] = "Pause";

            _fallbackStrings["tr-TR:Resume"] = "Devam Et";
            _fallbackStrings["en-US:Resume"] = "Resume";

            _fallbackStrings["tr-TR:Cancel"] = "İptal";
            _fallbackStrings["en-US:Cancel"] = "Cancel";

            _fallbackStrings["tr-TR:Stop"] = "Durdur";
            _fallbackStrings["en-US:Stop"] = "Stop";

            _fallbackStrings["tr-TR:Retry"] = "Yeniden Dene";
            _fallbackStrings["en-US:Retry"] = "Retry";

            _fallbackStrings["tr-TR:Failed"] = "Başarısız";
            _fallbackStrings["en-US:Failed"] = "Failed";

            _fallbackStrings["tr-TR:Completed"] = "Tamamlandı";
            _fallbackStrings["en-US:Completed"] = "Completed";

            _fallbackStrings["tr-TR:Copying"] = "Kopyalanıyor";
            _fallbackStrings["en-US:Copying"] = "Copying";

            _fallbackStrings["tr-TR:RemainingTime"] = "Kalan Süre";
            _fallbackStrings["en-US:RemainingTime"] = "Remaining Time";

            _fallbackStrings["tr-TR:TransferSpeed"] = "Aktarım Hızı";
            _fallbackStrings["en-US:TransferSpeed"] = "Transfer Speed";

            _fallbackStrings["tr-TR:Verification"] = "Doğrulama";
            _fallbackStrings["en-US:Verification"] = "Verification";

            _fallbackStrings["tr-TR:BandwidthLimit"] = "Bant Genişliği Sınırı";
            _fallbackStrings["en-US:BandwidthLimit"] = "Bandwidth Limit";

            _fallbackStrings["tr-TR:Language"] = "Dil";
            _fallbackStrings["en-US:Language"] = "Language";

            _fallbackStrings["tr-TR:Settings_Title"] = "Uygulama Ayarları";
            _fallbackStrings["en-US:Settings_Title"] = "Application Settings";

            _fallbackStrings["tr-TR:Settings_Save"] = "Ayarları Kaydet";
            _fallbackStrings["en-US:Settings_Save"] = "Save Settings";

            _fallbackStrings["tr-TR:Settings_Saved_Title"] = "Ayarlar Kaydedildi";
            _fallbackStrings["en-US:Settings_Saved_Title"] = "Settings Saved";

            _fallbackStrings["tr-TR:Settings_Saved_Message"] = "Uygulama ayarları başarıyla güncellendi.";
            _fallbackStrings["en-US:Settings_Saved_Message"] = "Application settings updated successfully.";

            // Phase 2.5 Fallback Strings
            _fallbackStrings["tr-TR:ActiveJobsTitle"] = "Aktif Kopyalama Görevleri";
            _fallbackStrings["en-US:ActiveJobsTitle"] = "Active Copy Jobs";

            _fallbackStrings["tr-TR:StatusPaused"] = "DURAKLATILDI";
            _fallbackStrings["en-US:StatusPaused"] = "PAUSED";

            _fallbackStrings["tr-TR:BandwidthLimitTitle"] = "Aktarım Hızı Sınırı";
            _fallbackStrings["en-US:BandwidthLimitTitle"] = "Transfer Speed Limit";

            _fallbackStrings["tr-TR:VerificationSha256"] = "SHA-256";
            _fallbackStrings["en-US:VerificationSha256"] = "SHA-256";

            _fallbackStrings["tr-TR:RecoveryActionResume"] = "Devam Et";
            _fallbackStrings["en-US:RecoveryActionResume"] = "Resume";

            _fallbackStrings["tr-TR:Str_UsbWaiting"] = "Hedef sürücü bekleniyor";
            _fallbackStrings["en-US:Str_UsbWaiting"] = "Waiting for destination drive";
        }
    }
}
