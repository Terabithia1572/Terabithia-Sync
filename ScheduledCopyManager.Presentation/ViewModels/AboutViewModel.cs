using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class AboutViewModel : ObservableObject
    {
        public string AppName => "Terabithia Sync";
        public string Description => "Zamanlı Dosya ve Klasör Kopyalama Yöneticisi";
        public string Publisher => "Yunus İNAN";
        public string Version => "1.0.0";
        public string Copyright => "© Yunus İNAN - Tüm Hakları Saklıdır.";
        public string Platform => "Windows 10 / Windows 11 (64-bit)";
        public string Runtime => "Bağımsız (Self-contained .NET 8)";
        public string ApplicationType => "WPF Masaüstü Uygulaması";

        public List<string> Features { get; } = new()
        {
            "Otomatik zamanlanmış kopyalama (Günlük, Haftalık, Aylık, Özel Cron)",
            "Gelişmiş kök klasör yapısını koruyan dosya ve klasör yedekleme",
            "Otomatik tak-çalıştır USB taşınabilir sürücü desteği",
            "Akıllı artımlı kopyalama (İçerik/zaman damgası karşılaştırma)",
            "Güvenli ayna kopyalama (Hedef klasör koruma alanlı silme izni)",
            "Çakışma yönetimi (Atla, Üzerine Yaz, Yeniden Adlandır)",
            "Kriptografik SHA-256 bütünlük doğrulaması",
            "Otomatik yeniden deneme ve hata tolerans mekanizması",
            "Zengin Windows toast bildirimleri ve sistem tepsisi (Tray) entegrasyonu",
            "Ayrıntılı işlem geçmişi ve filtrelenebilir günlük kayıt sistemi"
        };
    }
}
