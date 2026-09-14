using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Presentation.Services;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class JobEditorViewModel : ObservableObject
    {
        private readonly IPathValidationService _pathValidationService;
        private readonly IDialogService _dialogService;
        private readonly IFileCopyService? _fileCopyService;

        public Job EditingJob { get; }

        public event Action<Job?>? RequestClose;

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _destinationPath = string.Empty;
        [ObservableProperty] private bool _enabled = true;
        [ObservableProperty] private ScheduleType _selectedScheduleType = ScheduleType.Daily;
        [ObservableProperty] private TimeSpan _timeOfDay = new(8, 0, 0);
        [ObservableProperty] private int _dayOfMonth = 1;
        [ObservableProperty] private DateTime? _startDate = DateTime.Now;
        [ObservableProperty] private DateTime? _endDate;
        [ObservableProperty] private string? _cronExpression;
        [ObservableProperty] private MissedJobBehavior _selectedMissedJobBehavior = MissedJobBehavior.RunImmediately;
        [ObservableProperty] private CopyMode _selectedCopyMode = CopyMode.Incremental;
        [ObservableProperty] private ConflictPolicy _selectedConflictPolicy = ConflictPolicy.Overwrite;
        [ObservableProperty] private int _retryCount = 3;
        [ObservableProperty] private int _retryDelaySeconds = 5;
        [ObservableProperty] private bool _verifyCopy = false;
        [ObservableProperty] private bool _preserveTimestamps = true;
        [ObservableProperty] private bool _preserveAttributes = true;
        [ObservableProperty] private bool _createDestinationIfMissing = true;
        [ObservableProperty] private bool _continueOnError = true;
        [ObservableProperty] private bool _enableMirrorDeletion = false;
        [ObservableProperty] private bool _isUsbDestination = false;
        [ObservableProperty] private string _copyModeExplanation = string.Empty;
        [ObservableProperty] private string _conflictPolicyExplanation = string.Empty;

        [ObservableProperty] private string? _validationErrorMessage;
        [ObservableProperty] private string? _testResultMessage;
        [ObservableProperty] private bool _isTesting = false;

        // Days of week toggles
        [ObservableProperty] private bool _dayMonday = true;
        [ObservableProperty] private bool _dayTuesday = true;
        [ObservableProperty] private bool _dayWednesday = true;
        [ObservableProperty] private bool _dayThursday = true;
        [ObservableProperty] private bool _dayFriday = true;
        [ObservableProperty] private bool _daySaturday = false;
        [ObservableProperty] private bool _daySunday = false;

        public ObservableCollection<string> SourcePaths { get; } = new();
        public ObservableCollection<SourceItemViewModel> SourceItems { get; } = new();

        public Array ScheduleTypes => Enum.GetValues(typeof(ScheduleType));
        public Array MissedJobBehaviors => Enum.GetValues(typeof(MissedJobBehavior));
        public Array CopyModes => Enum.GetValues(typeof(CopyMode));
        public Array ConflictPolicies => Enum.GetValues(typeof(ConflictPolicy));

        partial void OnSelectedCopyModeChanged(CopyMode value)
        {
            UpdateCopyModeExplanation(value);
        }

        partial void OnSelectedConflictPolicyChanged(ConflictPolicy value)
        {
            UpdateConflictPolicyExplanation(value);
        }

        private void UpdateCopyModeExplanation(CopyMode mode)
        {
            CopyModeExplanation = mode switch
            {
                CopyMode.Incremental => "Artımlı Kopyalama:\nYalnızca hedefte bulunmayan veya kaynakta hedefteki kopyasından daha yeni/değişmiş olan dosyaları kopyalar. Hedefte bulunan ve güncel olan dosyalar gereksiz yere tekrar kopyalanmaz. Büyük klasörlerin düzenli yedeklenmesi için uygundur.",
                CopyMode.Mirror => "Ayna Modu:\nKaynak klasörün hedefte mümkün olduğunca birebir aynısını oluşturur. Kaynakta bulunan dosya ve klasörler hedefe kopyalanır. 'Ayna Modu Silme İzni' etkinleştirilirse, kaynakta artık bulunmayan ancak hedefte bulunan dosyalar da hedef klasörden silinir.",
                CopyMode.VerifyOnly => "Yalnızca Doğrula:\nDosyaları kopyalamadan kaynak ve hedef arasındaki durumu kontrol eder. Dosyaların mevcut olup olmadığını ve doğrulama koşullarını incelemek için kullanılır. Gerçek kopyalama işlemi gerçekleştirilmez.",
                _ => string.Empty
            };
        }

        private void UpdateConflictPolicyExplanation(ConflictPolicy policy)
        {
            ConflictPolicyExplanation = policy switch
            {
                ConflictPolicy.Skip => "Atla:\nDosya hedefte zaten mevcutsa mevcut dosyaya dokunulmaz ve kaynak dosya kopyalanmaz.",
                ConflictPolicy.Overwrite => "Üzerine Yaz:\nHedefte aynı ada sahip bir dosya varsa kaynak dosya hedefteki dosyanın üzerine yazılır.\n⚠️ Dikkat: Hedefteki mevcut dosyanın içeriği değiştirilebilir.",
                ConflictPolicy.Rename => "Yeniden Adlandır:\nHedefte aynı ada sahip bir dosya varsa mevcut dosyanın üzerine yazılmaz. Yeni dosya otomatik olarak farklı bir adla kaydedilir.\nÖrnek: rapor.xlsx → rapor (1).xlsx",
                _ => string.Empty
            };
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                return full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd('\\', '/');
            }
        }

        private bool IsPathAlreadyAdded(string newPath)
        {
            string normNew = NormalizePath(newPath);
            return SourceItems.Any(existing => string.Equals(NormalizePath(existing.Path), normNew, StringComparison.OrdinalIgnoreCase));
        }

        public JobEditorViewModel(
            IPathValidationService pathValidationService,
            IDialogService dialogService,
            IFileCopyService? fileCopyService = null,
            Job? existingJob = null)
        {
            _pathValidationService = pathValidationService ?? throw new ArgumentNullException(nameof(pathValidationService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _fileCopyService = fileCopyService;

            if (existingJob != null)
            {
                EditingJob = existingJob;
                Name = existingJob.Name;
                DestinationPath = existingJob.DestinationPath;
                Enabled = existingJob.Enabled;
                SelectedScheduleType = existingJob.Schedule.ScheduleType;
                TimeOfDay = existingJob.Schedule.TimeOfDay;
                DayOfMonth = existingJob.Schedule.DayOfMonth;
                StartDate = existingJob.Schedule.StartDate;
                EndDate = existingJob.Schedule.EndDate;
                CronExpression = existingJob.Schedule.CronExpression;
                SelectedMissedJobBehavior = existingJob.MissedJobBehavior;
                SelectedCopyMode = existingJob.CopyMode;
                SelectedConflictPolicy = existingJob.ConflictPolicy;
                RetryCount = existingJob.RetryCount;
                RetryDelaySeconds = (int)existingJob.RetryDelay.TotalSeconds;
                VerifyCopy = existingJob.VerifyCopy;
                PreserveTimestamps = existingJob.PreserveTimestamps;
                PreserveAttributes = existingJob.PreserveAttributes;
                CreateDestinationIfMissing = existingJob.CreateDestinationIfMissing;
                ContinueOnError = existingJob.ContinueOnError;
                EnableMirrorDeletion = existingJob.EnableMirrorDeletion;
                IsUsbDestination = existingJob.IsUsbDestination;

                foreach (var p in existingJob.SourcePaths)
                {
                    SourcePaths.Add(p);
                    SourceItems.Add(new SourceItemViewModel(p));
                }

                if (existingJob.Schedule.DaysOfWeek?.Length == 7)
                {
                    DaySunday = existingJob.Schedule.DaysOfWeek[0];
                    DayMonday = existingJob.Schedule.DaysOfWeek[1];
                    DayTuesday = existingJob.Schedule.DaysOfWeek[2];
                    DayWednesday = existingJob.Schedule.DaysOfWeek[3];
                    DayThursday = existingJob.Schedule.DaysOfWeek[4];
                    DayFriday = existingJob.Schedule.DaysOfWeek[5];
                    DaySaturday = existingJob.Schedule.DaysOfWeek[6];
                }
            }
            else
            {
                EditingJob = new Job();
                Name = "Yeni Kopyalama Görevi";
            }

            UpdateCopyModeExplanation(SelectedCopyMode);
            UpdateConflictPolicyExplanation(SelectedConflictPolicy);
        }

        [RelayCommand]
        public void AddSourceFile()
        {
            var files = _dialogService.SelectFiles("Kaynak Dosya Seçin");
            foreach (var f in files)
            {
                if (!string.IsNullOrWhiteSpace(f) && !IsPathAlreadyAdded(f))
                {
                    SourcePaths.Add(f);
                    SourceItems.Add(new SourceItemViewModel(f));
                }
            }
        }

        [RelayCommand]
        public void AddSourceFolder()
        {
            var folders = _dialogService.SelectFolders("Kaynak Klasör Seçin");
            foreach (var folder in folders)
            {
                if (!string.IsNullOrWhiteSpace(folder) && !IsPathAlreadyAdded(folder))
                {
                    SourcePaths.Add(folder);
                    SourceItems.Add(new SourceItemViewModel(folder));
                }
            }
        }

        [RelayCommand]
        public async Task RemoveSelectedSourcesAsync(object? param)
        {
            List<SourceItemViewModel> itemsToRemove = new();
            if (param is System.Collections.IList list)
            {
                foreach (var item in list)
                {
                    if (item is SourceItemViewModel srcVm) itemsToRemove.Add(srcVm);
                    else if (item is string str)
                    {
                        var match = SourceItems.FirstOrDefault(x => x.Path == str);
                        if (match != null) itemsToRemove.Add(match);
                    }
                }
            }
            else if (param is SourceItemViewModel single)
            {
                itemsToRemove.Add(single);
            }
            else if (param is string strPath)
            {
                var match = SourceItems.FirstOrDefault(x => x.Path == strPath);
                if (match != null) itemsToRemove.Add(match);
            }

            if (!itemsToRemove.Any()) return;

            if (itemsToRemove.Count > 1)
            {
                bool confirm = await _dialogService.ShowConfirmationAsync(
                    "Kaynakları Kaldır",
                    $"Seçili {itemsToRemove.Count} kaynağı listeden kaldırmak istediğinize emin misiniz?\n\n(Bu işlem bilgisayarınızdaki gerçek dosyaları silmez, yalnızca görev konfigürasyonundan kaldırır.)");
                if (!confirm) return;
            }

            foreach (var item in itemsToRemove)
            {
                SourceItems.Remove(item);
                SourcePaths.Remove(item.Path);
            }
        }

        [RelayCommand]
        public async Task ClearAllSourcesAsync()
        {
            if (!SourceItems.Any()) return;

            bool confirm = await _dialogService.ShowConfirmationAsync(
                "Tüm Kaynakları Kaldır",
                "Listedeki tüm kaynak dosya ve klasörleri kaldırmak istediğinize emin misiniz?\n\n(Bu işlem bilgisayarınızdaki gerçek dosyaları silmez, yalnızca görev konfigürasyonundan kaldırır.)");

            if (confirm)
            {
                SourceItems.Clear();
                SourcePaths.Clear();
            }
        }

        [RelayCommand]
        public void RemoveSourcePath(string path)
        {
            if (SourcePaths.Contains(path))
            {
                SourcePaths.Remove(path);
            }
        }

        [RelayCommand]
        public void BrowseDestinationFolder()
        {
            var folder = _dialogService.SelectFolder("Hedef Klasör Seçin");
            if (!string.IsNullOrEmpty(folder))
            {
                DestinationPath = folder;
            }
        }

        [RelayCommand]
        public async Task TestJobAsync()
        {
            ValidationErrorMessage = null;
            TestResultMessage = null;
            IsTesting = true;

            try
            {
                if (!SourcePaths.Any())
                {
                    ValidationErrorMessage = "En az bir kaynak dosya veya klasör seçmelisiniz.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(DestinationPath))
                {
                    ValidationErrorMessage = "Hedef klasör belirtilmelidir.";
                    return;
                }

                var errors = await _pathValidationService.ValidateJobPathsAsync(SourcePaths, DestinationPath);
                if (errors.Any())
                {
                    ValidationErrorMessage = string.Join("\n", errors);
                    return;
                }

                if (_fileCopyService != null)
                {
                    var testJob = BuildJobFromForm();
                    var result = await _fileCopyService.CopyAsync(testJob, dryRun: true, progress: null, CancellationToken.None);

                    if (result.Success)
                    {
                        TestResultMessage = $"Ön İzleme Başarılı: {result.FilesCopied} dosya ön izleme modunda kontrol edildi ({result.BytesCopied / 1024} KB).";
                    }
                    else
                    {
                        ValidationErrorMessage = $"Ön İzleme Hatalı: {string.Join(", ", result.Errors)}";
                    }
                }
                else
                {
                    TestResultMessage = "Ön İzleme Başarılı: Seçilen yollar geçerli ve erişilebilir.";
                }
            }
            catch (Exception ex)
            {
                ValidationErrorMessage = $"Test sırasında hata oluştu: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        [RelayCommand]
        public async Task SaveAsync()
        {
            ValidationErrorMessage = null;

            if (string.IsNullOrWhiteSpace(Name))
            {
                ValidationErrorMessage = "Görev adı boş bırakılamaz.";
                return;
            }

            if (!SourcePaths.Any())
            {
                ValidationErrorMessage = "En az bir kaynak dosya veya klasör seçmelisiniz.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DestinationPath))
            {
                ValidationErrorMessage = "Hedef klasör belirtilmelidir.";
                return;
            }

            var validationErrors = await _pathValidationService.ValidateJobPathsAsync(SourcePaths, DestinationPath);
            if (validationErrors.Any())
            {
                ValidationErrorMessage = string.Join("\n", validationErrors);
                return;
            }

            // Safety Warning for Mirror Mode Deletion
            if (SelectedCopyMode == CopyMode.Mirror && EnableMirrorDeletion)
            {
                bool confirm = await _dialogService.ShowConfirmationAsync(
                    "UYARI - Ayna Modu Silme İzni",
                    "UYARI:\n\nAyna modu, hedef klasörde kaynakta bulunmayan dosyaların kalıcı olarak silinmesine neden olabilir.\n\nBu işlemin geri alınması mümkün olmayabilir.\n\nDevam etmek istediğinize emin misiniz?");

                if (!confirm)
                {
                    return;
                }
            }

            // Apply properties to EditingJob
            var job = BuildJobFromForm();
            EditingJob.Name = job.Name;
            EditingJob.SourcePaths = job.SourcePaths;
            EditingJob.DestinationPath = job.DestinationPath;
            EditingJob.Enabled = job.Enabled;
            EditingJob.Schedule = job.Schedule;
            EditingJob.MissedJobBehavior = job.MissedJobBehavior;
            EditingJob.CopyMode = job.CopyMode;
            EditingJob.ConflictPolicy = job.ConflictPolicy;
            EditingJob.RetryCount = job.RetryCount;
            EditingJob.RetryDelay = job.RetryDelay;
            EditingJob.VerifyCopy = job.VerifyCopy;
            EditingJob.PreserveTimestamps = job.PreserveTimestamps;
            EditingJob.PreserveAttributes = job.PreserveAttributes;
            EditingJob.CreateDestinationIfMissing = job.CreateDestinationIfMissing;
            EditingJob.ContinueOnError = job.ContinueOnError;
            EditingJob.EnableMirrorDeletion = job.EnableMirrorDeletion;
            EditingJob.IsUsbDestination = job.IsUsbDestination;

            RequestClose?.Invoke(EditingJob);
        }

        [RelayCommand]
        public void Cancel()
        {
            RequestClose?.Invoke(null);
        }

        private Job BuildJobFromForm()
        {
            return new Job
            {
                Id = EditingJob.Id,
                Name = Name.Trim(),
                SourcePaths = SourcePaths.ToList(),
                DestinationPath = DestinationPath.Trim(),
                Enabled = Enabled,
                Schedule = new JobSchedule
                {
                    ScheduleType = SelectedScheduleType,
                    TimeOfDay = TimeOfDay,
                    DayOfMonth = DayOfMonth,
                    StartDate = StartDate,
                    EndDate = EndDate,
                    CronExpression = CronExpression,
                    DaysOfWeek = new bool[7]
                    {
                        DaySunday, DayMonday, DayTuesday, DayWednesday, DayThursday, DayFriday, DaySaturday
                    }
                },
                MissedJobBehavior = SelectedMissedJobBehavior,
                CopyMode = SelectedCopyMode,
                ConflictPolicy = SelectedConflictPolicy,
                RetryCount = Math.Max(1, RetryCount),
                RetryDelay = TimeSpan.FromSeconds(Math.Max(1, RetryDelaySeconds)),
                VerifyCopy = VerifyCopy,
                PreserveTimestamps = PreserveTimestamps,
                PreserveAttributes = PreserveAttributes,
                CreateDestinationIfMissing = CreateDestinationIfMissing,
                ContinueOnError = ContinueOnError,
                EnableMirrorDeletion = EnableMirrorDeletion,
                IsUsbDestination = IsUsbDestination
            };
        }
    }
}
