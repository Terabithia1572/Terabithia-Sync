using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        [ObservableProperty] private string? _targetVolumeSerialNumber;
        [ObservableProperty] private string? _targetVolumeLabel;
        [ObservableProperty] private UsbDriveOptionViewModel? _selectedUsbDrive;
        [ObservableProperty] private string? _usbStatusMessage;
        public bool HasUsbStatusMessage => !string.IsNullOrWhiteSpace(UsbStatusMessage);
        public ObservableCollection<UsbDriveOptionViewModel> AvailableUsbDrives { get; } = new();

        [ObservableProperty] private string _copyModeExplanation = string.Empty;
        [ObservableProperty] private string _conflictPolicyExplanation = string.Empty;

        // Bandwidth Limit UI Properties
        [ObservableProperty] private string _selectedBandwidthPreset = "Sınırsız";
        [ObservableProperty] private double _bandwidthLimitMBps = 10;
        [ObservableProperty] private bool _isCustomBandwidthVisible = false;

        // Retry Policy UI Properties
        [ObservableProperty] private bool _enableAutoRetry = true;
        [ObservableProperty] private int _maxRetryAttempts = 3;
        [ObservableProperty] private bool _useExponentialBackoff = true;
        [ObservableProperty] private bool _retryLockedFiles = true;
        [ObservableProperty] private bool _waitForDestination = true;
        [ObservableProperty] private bool _resumeWhenDestinationReturns = true;
        [ObservableProperty] private FailureBehavior _selectedFailureBehavior = FailureBehavior.ContinueWithRemaining;

        // Verification Mode UI Property
        [ObservableProperty] private VerificationMode _selectedVerificationMode = VerificationMode.None;

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
        public string[] BandwidthPresets => new[] { "Sınırsız", "5 MB/s", "10 MB/s", "25 MB/s", "50 MB/s", "100 MB/s", "Özel" };
        public Array FailureBehaviors => Enum.GetValues(typeof(FailureBehavior));
        public Array VerificationModes => Enum.GetValues(typeof(VerificationMode));

        public string SummaryScheduleText => SelectedScheduleType switch
        {
            ScheduleType.Daily => $"Her Gün {TimeOfDay:hh\\:mm}",
            ScheduleType.Weekly => $"Haftalık {TimeOfDay:hh\\:mm}",
            ScheduleType.Monthly => $"Her Ayın {DayOfMonth}. Günü {TimeOfDay:hh\\:mm}",
            ScheduleType.OneTime => $"Tek Seferlik ({StartDate:dd.MM.yyyy HH:mm})",
            ScheduleType.Cron => $"Cron: {CronExpression}",
            _ => "Belirtilmedi"
        };

        public string SummaryCopyModeText => SelectedCopyMode switch
        {
            CopyMode.Incremental => "Artımlı (Incremental)",
            CopyMode.Mirror => EnableMirrorDeletion ? "Ayna Modu (Silme Etkin)" : "Ayna Modu (Silme Kapalı)",
            CopyMode.VerifyOnly => "Yalnızca Doğrula",
            _ => SelectedCopyMode.ToString()
        };

        public string SummaryConflictPolicyText => SelectedConflictPolicy switch
        {
            ConflictPolicy.Skip => "Atla",
            ConflictPolicy.Overwrite => "Üzerine Yaz",
            ConflictPolicy.Rename => "Yeniden Adlandır",
            _ => SelectedConflictPolicy.ToString()
        };

        public string SummaryVerificationText => SelectedVerificationMode switch
        {
            VerificationMode.None => "Yok",
            VerificationMode.SizeAndTimestamp => "Hızlı (Boyut & Tarih)",
            VerificationMode.SHA256 => "Tam (SHA-256 Hash)",
            _ => SelectedVerificationMode.ToString()
        };

        partial void OnSelectedCopyModeChanged(CopyMode value)
        {
            UpdateCopyModeExplanation(value);
            OnPropertyChanged(nameof(SummaryCopyModeText));
        }

        partial void OnSelectedConflictPolicyChanged(ConflictPolicy value)
        {
            UpdateConflictPolicyExplanation(value);
            OnPropertyChanged(nameof(SummaryConflictPolicyText));
        }

        partial void OnSelectedScheduleTypeChanged(ScheduleType value)
        {
            OnPropertyChanged(nameof(SummaryScheduleText));
        }

        partial void OnSelectedVerificationModeChanged(VerificationMode value)
        {
            VerifyCopy = value == VerificationMode.SHA256;
            OnPropertyChanged(nameof(SummaryVerificationText));
        }

        partial void OnSelectedBandwidthPresetChanged(string value)
        {
            IsCustomBandwidthVisible = value == "Özel";
        }

        partial void OnIsUsbDestinationChanged(bool value)
        {
            if (value)
            {
                LoadUsbDrives();
            }
        }

        private string? _previousDriveSerial;

        partial void OnSelectedUsbDriveChanged(UsbDriveOptionViewModel? value)
        {
            if (value == null || !IsUsbDestination) return;

            if (value.IsPresent && !string.IsNullOrEmpty(value.VolumeSerialNumber))
            {
                TargetVolumeSerialNumber = value.VolumeSerialNumber;
                TargetVolumeLabel = value.VolumeLabel;

                string targetLetter = value.DriveLetter.EndsWith("\\") ? value.DriveLetter : value.DriveLetter + "\\";

                bool isDifferentVolume = !string.Equals(_previousDriveSerial, value.VolumeSerialNumber, StringComparison.OrdinalIgnoreCase);
                _previousDriveSerial = value.VolumeSerialNumber;

                if (string.IsNullOrWhiteSpace(DestinationPath))
                {
                    DestinationPath = targetLetter;
                }
                else if (isDifferentVolume)
                {
                    try
                    {
                        string currentRoot = Path.GetPathRoot(DestinationPath) ?? string.Empty;
                        if (!string.IsNullOrEmpty(currentRoot) && DestinationPath.Length > currentRoot.Length)
                        {
                            string relative = DestinationPath.Substring(currentRoot.Length).TrimStart('\\', '/');
                            DestinationPath = Path.Combine(targetLetter, relative);
                        }
                        else
                        {
                            DestinationPath = targetLetter;
                        }
                    }
                    catch
                    {
                        DestinationPath = targetLetter;
                    }
                }
                else
                {
                    try
                    {
                        string currentRoot = Path.GetPathRoot(DestinationPath) ?? string.Empty;
                        if (!string.IsNullOrEmpty(currentRoot) && !string.Equals(currentRoot.TrimEnd('\\'), targetLetter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            string relative = DestinationPath.Substring(currentRoot.Length).TrimStart('\\', '/');
                            DestinationPath = Path.Combine(targetLetter, relative);
                        }
                    }
                    catch { }
                }
            }
        }

        [RelayCommand]
        public void RefreshUsbDrives()
        {
            LoadUsbDrives();
        }

        public void LoadUsbDrives()
        {
            string? previousSelectedSerial = SelectedUsbDrive?.VolumeSerialNumber ?? TargetVolumeSerialNumber;
            AvailableUsbDrives.Clear();
            UsbStatusMessage = null;

            if (_usbDriveService == null)
            {
                UsbStatusMessage = "Bağlı USB sürücüsü bulunamadı.";
                _logService?.LogInformation("[USB SELECTOR REFRESH] UsbDriveService is null. AvailableUsbDrives=0");
                OnPropertyChanged(nameof(HasUsbStatusMessage));
                return;
            }

            var drives = _usbDriveService.GetRemovableDrives();
            UsbDriveOptionViewModel? matchedOption = null;

            int totalDetected = drives.Count;
            int eligibleCount = 0;

            foreach (var d in drives)
            {
                var opt = new UsbDriveOptionViewModel
                {
                    DriveLetter = d.Name,
                    VolumeLabel = d.VolumeLabel,
                    VolumeSerialNumber = d.VolumeSerialNumber,
                    TotalSize = d.TotalSize,
                    AvailableFreeSpace = d.AvailableFreeSpace,
                    IsPresent = true
                };
                AvailableUsbDrives.Add(opt);
                eligibleCount++;

                if (!string.IsNullOrEmpty(previousSelectedSerial) &&
                    string.Equals(d.VolumeSerialNumber, previousSelectedSerial, StringComparison.OrdinalIgnoreCase))
                {
                    matchedOption = opt;
                }
            }

            if (AvailableUsbDrives.Count == 0)
            {
                UsbStatusMessage = "Bağlı USB sürücüsü bulunamadı.";
            }

            if (matchedOption == null && !string.IsNullOrEmpty(TargetVolumeSerialNumber))
            {
                matchedOption = new UsbDriveOptionViewModel
                {
                    DriveLetter = !string.IsNullOrEmpty(DestinationPath) ? Path.GetPathRoot(DestinationPath) ?? "USB:" : "USB:",
                    VolumeLabel = TargetVolumeLabel ?? string.Empty,
                    VolumeSerialNumber = TargetVolumeSerialNumber,
                    IsPresent = false
                };
                AvailableUsbDrives.Insert(0, matchedOption);
                UsbStatusMessage = "Kayıtlı USB şu anda bağlı değil";
            }

            if (matchedOption != null)
            {
                SelectedUsbDrive = matchedOption;
            }
            else if (AvailableUsbDrives.Count > 0)
            {
                var dataDrives = AvailableUsbDrives.Where(d => d.IsPresent && d.TotalSize > 100 * 1024 * 1024).ToList();
                if (dataDrives.Any())
                {
                    SelectedUsbDrive = dataDrives.OrderByDescending(d => d.TotalSize).First();
                    foreach (var opt in AvailableUsbDrives.Where(d => d.IsPresent && d.TotalSize <= 100 * 1024 * 1024))
                    {
                        _logService?.LogInformation($"[USB SELECTOR FILTER] Root={opt.DriveLetter} Label={opt.VolumeLabel} Reason=Volume size under 100MB helper partition ({opt.TotalSize / 1024 / 1024} MB)");
                    }
                }
                else
                {
                    SelectedUsbDrive = AvailableUsbDrives.FirstOrDefault();
                }
            }
            else
            {
                SelectedUsbDrive = null;
            }

            _logService?.LogInformation($"[USB SELECTOR REFRESH] DetectedVolumes={totalDetected} EligibleTargets={eligibleCount} SelectedSerial={SelectedUsbDrive?.VolumeSerialNumber ?? "None"} SelectedRoot={SelectedUsbDrive?.DriveLetter ?? "None"}");

            OnPropertyChanged(nameof(HasUsbStatusMessage));
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
                string full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

        private readonly IPathValidationService _pathValidationService;
        private readonly IPreflightValidationService _preflightValidationService;
        private readonly IDialogService _dialogService;
        private readonly IFileCopyService? _fileCopyService;
        private readonly IJobRepository? _jobRepository;
        private readonly IUsbDriveService? _usbDriveService;
        private readonly ILogService? _logService;

        public Job EditingJob { get; }

        public event Action<Job?>? RequestClose;

        public JobEditorViewModel(
            IPathValidationService pathValidationService,
            IDialogService dialogService,
            IFileCopyService? fileCopyService = null,
            Job? existingJob = null,
            IPreflightValidationService? preflightValidationService = null,
            IJobRepository? jobRepository = null,
            IUsbDriveService? usbDriveService = null,
            ILogService? logService = null)
        {
            _pathValidationService = pathValidationService ?? throw new ArgumentNullException(nameof(pathValidationService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _fileCopyService = fileCopyService;
            _preflightValidationService = preflightValidationService ?? new Infrastructure.Services.PreflightValidationService(_pathValidationService);
            _jobRepository = jobRepository;
            _usbDriveService = usbDriveService;
            _logService = logService;

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
                TargetVolumeSerialNumber = existingJob.TargetVolumeSerialNumber;
                TargetVolumeLabel = existingJob.TargetVolumeLabel;
                IsUsbDestination = existingJob.IsUsbDestination;

                foreach (var p in existingJob.SourcePaths)
                {
                    SourcePaths.Add(p);
                    SourceItems.Add(new SourceItemViewModel(p));
                }

                if (existingJob.BandwidthLimit != null)
                {
                    if (!existingJob.BandwidthLimit.Enabled || existingJob.BandwidthLimit.MegabytesPerSecond <= 0)
                    {
                        SelectedBandwidthPreset = "Sınırsız";
                    }
                    else if (existingJob.BandwidthLimit.MegabytesPerSecond == 5) SelectedBandwidthPreset = "5 MB/s";
                    else if (existingJob.BandwidthLimit.MegabytesPerSecond == 10) SelectedBandwidthPreset = "10 MB/s";
                    else if (existingJob.BandwidthLimit.MegabytesPerSecond == 25) SelectedBandwidthPreset = "25 MB/s";
                    else if (existingJob.BandwidthLimit.MegabytesPerSecond == 50) SelectedBandwidthPreset = "50 MB/s";
                    else if (existingJob.BandwidthLimit.MegabytesPerSecond == 100) SelectedBandwidthPreset = "100 MB/s";
                    else
                    {
                        SelectedBandwidthPreset = "Özel";
                        BandwidthLimitMBps = existingJob.BandwidthLimit.MegabytesPerSecond;
                    }
                }

                if (existingJob.RetryPolicy != null)
                {
                    EnableAutoRetry = existingJob.RetryPolicy.Enabled;
                    MaxRetryAttempts = existingJob.RetryPolicy.MaxAttempts;
                    RetryDelaySeconds = existingJob.RetryPolicy.DelaySeconds;
                    UseExponentialBackoff = existingJob.RetryPolicy.UseExponentialBackoff;
                    RetryLockedFiles = existingJob.RetryPolicy.RetryLockedFiles;
                    WaitForDestination = existingJob.RetryPolicy.WaitForDestination;
                    ResumeWhenDestinationReturns = existingJob.RetryPolicy.ResumeWhenDestinationReturns;
                    SelectedFailureBehavior = existingJob.RetryPolicy.FailureBehavior;
                }

                SelectedVerificationMode = existingJob.VerificationMode;
                if (SelectedVerificationMode == VerificationMode.None && existingJob.VerifyCopy)
                {
                    SelectedVerificationMode = VerificationMode.SHA256;
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
            if (IsUsbDestination)
            {
                LoadUsbDrives();
            }
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
                var testJob = BuildJobFromForm();
                var preflight = await _preflightValidationService.ValidateJobAsync(testJob);

                if (preflight.HasBlockingErrors)
                {
                    var blockingIssues = preflight.Issues.Where(i => i.Severity == PreflightSeverity.BlockingError);
                    ValidationErrorMessage = string.Join("\n", blockingIssues.Select(i => $"{i.Title}: {i.Message}"));
                    return;
                }

                if (_fileCopyService != null)
                {
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

            if (IsUsbDestination)
            {
                if (SelectedUsbDrive == null && string.IsNullOrEmpty(TargetVolumeSerialNumber))
                {
                    ValidationErrorMessage = "Lütfen kopyalama için geçerli bir USB sürücüsü seçin.";
                    return;
                }

                if (SelectedUsbDrive != null && SelectedUsbDrive.IsPresent)
                {
                    string usbLetter = SelectedUsbDrive.DriveLetter.TrimEnd('\\');
                    string destRoot = (Path.GetPathRoot(DestinationPath) ?? string.Empty).TrimEnd('\\');
                    if (!string.Equals(usbLetter, destRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        ValidationErrorMessage = $"Hedef klasör yolu ({DestinationPath}), seçili USB sürücüsü ({SelectedUsbDrive.DriveLetter}) üzerinde yer almalıdır.";
                        return;
                    }
                }
            }

            var tempJob = BuildJobFromForm();
            var preflight = await _preflightValidationService.ValidateJobAsync(tempJob);

            if (preflight.HasBlockingErrors)
            {
                var blockingIssues = preflight.Issues.Where(i => i.Severity == PreflightSeverity.BlockingError);
                ValidationErrorMessage = string.Join("\n", blockingIssues.Select(i => $"{i.Title}: {i.Message}"));
                return;
            }

            // Same-Name Job Warning (Requirement 9)
            if (_jobRepository != null)
            {
                var existingJobs = await _jobRepository.GetAllAsync();
                if (existingJobs.Any(j => j.Id != EditingJob.Id && string.Equals(j.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    bool confirmName = await _dialogService.ShowConfirmationAsync(
                        "Bu isimde başka bir görev zaten bulunuyor.",
                        "Aynı isimde birden fazla görev oluşturmak karışıklığa yol açabilir. Kaydetmeye devam etmek istiyor musunuz?");
                    if (!confirmName)
                    {
                        return;
                    }
                }
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
            EditingJob.TargetVolumeSerialNumber = job.TargetVolumeSerialNumber;
            EditingJob.TargetVolumeLabel = job.TargetVolumeLabel;
            EditingJob.BandwidthLimit = job.BandwidthLimit;
            EditingJob.RetryPolicy = job.RetryPolicy;
            EditingJob.VerificationMode = job.VerificationMode;

            RequestClose?.Invoke(EditingJob);
        }

        [RelayCommand]
        public void Cancel()
        {
            RequestClose?.Invoke(null);
        }

        private Job BuildJobFromForm()
        {
            double limitMBps = SelectedBandwidthPreset switch
            {
                "5 MB/s" => 5,
                "10 MB/s" => 10,
                "25 MB/s" => 25,
                "50 MB/s" => 50,
                "100 MB/s" => 100,
                "Özel" => Math.Max(0.1, BandwidthLimitMBps),
                _ => 0
            };

            var bandwidthLimit = new BandwidthLimit
            {
                Enabled = SelectedBandwidthPreset != "Sınırsız",
                MegabytesPerSecond = limitMBps
            };

            var retryPolicy = new RetryPolicy
            {
                Enabled = EnableAutoRetry,
                MaxAttempts = Math.Max(1, MaxRetryAttempts),
                DelaySeconds = Math.Max(1, RetryDelaySeconds),
                UseExponentialBackoff = UseExponentialBackoff,
                RetryLockedFiles = RetryLockedFiles,
                WaitForDestination = WaitForDestination,
                ResumeWhenDestinationReturns = ResumeWhenDestinationReturns,
                FailureBehavior = SelectedFailureBehavior
            };

            string? serial = TargetVolumeSerialNumber;
            string? label = TargetVolumeLabel;
            if (_usbDriveService != null && !string.IsNullOrWhiteSpace(DestinationPath))
            {
                string? currentSerial = _usbDriveService.GetVolumeSerialNumber(DestinationPath);
                if (!string.IsNullOrEmpty(currentSerial))
                {
                    serial = currentSerial;
                    var usbInfo = _usbDriveService.GetRemovableDrives().FirstOrDefault(d => string.Equals(d.VolumeSerialNumber, serial, StringComparison.OrdinalIgnoreCase));
                    if (usbInfo != null && !string.IsNullOrEmpty(usbInfo.VolumeLabel))
                    {
                        label = usbInfo.VolumeLabel;
                    }
                }
            }

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
                RetryCount = Math.Max(1, MaxRetryAttempts),
                RetryDelay = TimeSpan.FromSeconds(Math.Max(1, RetryDelaySeconds)),
                VerifyCopy = SelectedVerificationMode == VerificationMode.SHA256,
                PreserveTimestamps = PreserveTimestamps,
                PreserveAttributes = PreserveAttributes,
                CreateDestinationIfMissing = CreateDestinationIfMissing,
                ContinueOnError = ContinueOnError,
                EnableMirrorDeletion = EnableMirrorDeletion,
                IsUsbDestination = IsUsbDestination,
                TargetVolumeSerialNumber = serial,
                TargetVolumeLabel = label,
                BandwidthLimit = bandwidthLimit,
                RetryPolicy = retryPolicy,
                VerificationMode = SelectedVerificationMode
            };
        }
    }

    public class UsbDriveOptionViewModel
    {
        public string DriveLetter { get; set; } = string.Empty;
        public string VolumeLabel { get; set; } = string.Empty;
        public string VolumeSerialNumber { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public long AvailableFreeSpace { get; set; }
        public bool IsPresent { get; set; } = true;

        public string DisplayText
        {
            get
            {
                if (!IsPresent)
                {
                    string labelText = !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "Kayıtlı USB";
                    return $"Kayıtlı USB şu anda bağlı değil ({labelText} - {VolumeSerialNumber})";
                }

                string cleanLetter = DriveLetter.TrimEnd('\\');
                string labelStr = !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "Adsız Sürücü";
                string sizeStr = ScheduledCopyManager.Presentation.Helpers.FormattingHelpers.FormatBytes(TotalSize);
                return $"{cleanLetter} — {labelStr} — {sizeStr}";
            }
        }

        public override string ToString() => DisplayText;
    }
}
