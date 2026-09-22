using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Presentation.Helpers;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class RecoveryDetailsViewModel : ObservableObject
    {
        public JobCheckpoint Checkpoint { get; }
        public event Action? RequestClose;

        public string JobName => Checkpoint.JobName;
        public string DestinationPath => Checkpoint.DestinationPath;
        public string InterruptionReason
        {
            get
            {
                var reasonCode = Checkpoint.GetEffectiveInterruptionReason();
                return reasonCode switch
                {
                    ExecutionInterruptionReason.UserStopped => "Bu kopyalama kullanıcı tarafından durduruldu.",
                    ExecutionInterruptionReason.UserPaused => "Bu kopyalama kullanıcı tarafından duraklatıldı.",
                    ExecutionInterruptionReason.UserCancelled => "Bu kopyalama kullanıcı tarafından iptal edildi.",
                    ExecutionInterruptionReason.UnexpectedProcessExit => "Önceki kopyalama beklenmedik şekilde kesildi.",
                    ExecutionInterruptionReason.ApplicationCrash => "Önceki kopyalama çökme nedeniyle kesildi.",
                    ExecutionInterruptionReason.WindowsShutdown => "Kopyalama Windows kapanışı sırasında kesildi.",
                    ExecutionInterruptionReason.DestinationUnavailable => "Hedef sürücü bağlantısı kesildi.",
                    ExecutionInterruptionReason.Failure => "Kopyalama hatası oluştu.",
                    _ => string.IsNullOrWhiteSpace(Checkpoint.InterruptionReason) ? "Beklenmedik kesinti" : Checkpoint.InterruptionReason
                };
            }
        }
        public string LastUpdatedText => $"{Checkpoint.UpdatedAt:dd.MM.yyyy HH:mm:ss}";
        public string FilesSummary => $"{Checkpoint.CompletedFiles} / {Checkpoint.TotalFiles} dosya (Atlanan: {Checkpoint.SkippedFiles}, Hatalı: {Checkpoint.FailedFiles}, Bekleyen: {Checkpoint.PendingFiles})";
        public string BytesSummary => $"{FormattingHelpers.FormatBytes(Checkpoint.CompletedBytes)} / {FormattingHelpers.FormatBytes(Checkpoint.TotalBytes)}";

        public string LastStateText => Checkpoint.CurrentState switch
        {
            ExecutionState.Paused => "DURAKLATILDI",
            ExecutionState.Stopped => "DURDURULDU",
            ExecutionState.Cancelled => "İPTAL EDİLDİ",
            ExecutionState.Failed => "HATA",
            ExecutionState.DestinationUnavailable => "HEDEF BAĞLANTISI KESİLDİ",
            ExecutionState.Running => "KESİNTİYE UĞRADI",
            _ => "BİLİNMİYOR"
        };

        public string LastStateColor => Checkpoint.CurrentState switch
        {
            ExecutionState.Paused => "#D97706",
            ExecutionState.Stopped => "#6B21A8",
            ExecutionState.Cancelled => "#9333EA",
            ExecutionState.Failed => "#DC2626",
            ExecutionState.DestinationUnavailable => "#EF4444",
            _ => "#2563EB"
        };

        public RecoveryDetailsViewModel(JobCheckpoint checkpoint)
        {
            Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        }

        [RelayCommand]
        public void Close()
        {
            RequestClose?.Invoke();
        }
    }
}
