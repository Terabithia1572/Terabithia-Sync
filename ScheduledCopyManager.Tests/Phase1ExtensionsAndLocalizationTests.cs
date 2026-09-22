using System;
using System.Text.Json;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Presentation.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase1ExtensionsAndLocalizationTests
    {
        [Fact]
        public void RetryPolicy_Defaults_AreSetCorrectly()
        {
            var policy = new RetryPolicy();

            Assert.True(policy.Enabled);
            Assert.Equal(3, policy.MaxAttempts);
            Assert.Equal(5, policy.DelaySeconds);
            Assert.True(policy.UseExponentialBackoff);
            Assert.True(policy.RetryLockedFiles);
            Assert.True(policy.WaitForDestination);
            Assert.True(policy.ResumeWhenDestinationReturns);
            Assert.Equal(FailureBehavior.ContinueWithRemaining, policy.FailureBehavior);
        }

        [Fact]
        public void Settings_Defaults_AreSetCorrectly()
        {
            var settings = new Settings();

            Assert.Equal("tr-TR", settings.Language);
            Assert.Equal(VerificationMode.SizeAndTimestamp, settings.DefaultVerificationMode);
            Assert.NotNull(settings.DefaultRetryPolicy);
            Assert.Equal(3, settings.DefaultRetryPolicy.MaxAttempts);
            Assert.NotNull(settings.DefaultBandwidthLimit);
            Assert.False(settings.DefaultBandwidthLimit.Enabled);
            Assert.Equal(0, settings.DefaultBandwidthLimit.MegabytesPerSecond);
        }

        [Fact]
        public void Job_BackwardCompatibleDeserialization_AppliesSafeDefaultsForMissingProperties()
        {
            // Old Job JSON without RetryPolicy, VerificationMode, BandwidthLimit
            string oldJson = @"{
                ""Id"": ""3f2504e0-4f89-11d3-9a0c-0305e82c3301"",
                ""Name"": ""Legacy Backup Job"",
                ""SourcePaths"": [""C:\\Source""],
                ""DestinationPath"": ""D:\\Dest"",
                ""CopyMode"": 0,
                ""ConflictPolicy"": 1,
                ""Enabled"": true
            }";

            var job = JsonSerializer.Deserialize<Job>(oldJson);

            Assert.NotNull(job);
            Assert.Equal("Legacy Backup Job", job.Name);
            Assert.NotNull(job.RetryPolicy);
            Assert.Equal(3, job.RetryPolicy.MaxAttempts);
            Assert.Equal(VerificationMode.SizeAndTimestamp, job.VerificationMode);
            Assert.NotNull(job.BandwidthLimit);
            Assert.False(job.BandwidthLimit.Enabled);
        }

        [Fact]
        public void Settings_BackwardCompatibleDeserialization_AppliesSafeDefaultsForMissingProperties()
        {
            // Old Settings JSON without Language, DefaultVerificationMode, DefaultRetryPolicy, DefaultBandwidthLimit
            string oldJson = @"{
                ""StartWithWindows"": false,
                ""StartMinimized"": false,
                ""EnableTrayIcon"": true,
                ""CloseToTray"": true,
                ""EnableNotifications"": true,
                ""DefaultRetryCount"": 3,
                ""DefaultRetryDelaySeconds"": 5,
                ""DefaultConflictPolicy"": 1,
                ""MaxConcurrentJobs"": 2,
                ""IsFirstRun"": false
            }";

            var settings = JsonSerializer.Deserialize<Settings>(oldJson);

            Assert.NotNull(settings);
            Assert.Equal("tr-TR", settings.Language);
            Assert.Equal(VerificationMode.SizeAndTimestamp, settings.DefaultVerificationMode);
            Assert.NotNull(settings.DefaultRetryPolicy);
            Assert.Equal(3, settings.DefaultRetryPolicy.MaxAttempts);
            Assert.NotNull(settings.DefaultBandwidthLimit);
            Assert.False(settings.DefaultBandwidthLimit.Enabled);
        }

        [Fact]
        public void HistoryEntry_BackwardCompatibleDeserialization_AppliesNullForNewOptionalProperties()
        {
            // Old HistoryEntry JSON without Phase 1 properties
            string oldJson = @"{
                ""Id"": ""7a9b8c7d-1234-5678-90ab-cdef12345678"",
                ""JobId"": ""3f2504e0-4f89-11d3-9a0c-0305e82c3301"",
                ""JobName"": ""Legacy Job"",
                ""StartTime"": ""2026-09-19T10:00:00"",
                ""EndTime"": ""2026-09-19T10:05:00"",
                ""FilesCopied"": 10,
                ""FilesSkipped"": 2,
                ""FilesFailed"": 0,
                ""BytesCopied"": 1048576,
                ""Status"": 0,
                ""Message"": ""Successful""
            }";

            var entry = JsonSerializer.Deserialize<HistoryEntry>(oldJson);

            Assert.NotNull(entry);
            Assert.Equal("Legacy Job", entry.JobName);
            Assert.Equal(10, entry.FilesCopied);
            Assert.Null(entry.ScheduledStartTime);
            Assert.Null(entry.ActiveCopyDuration);
            Assert.Null(entry.AverageBytesPerSecond);
        }

        [Fact]
        public void VerificationMode_And_BandwidthLimit_Persistence_SerializesAndDeserializesCorrectly()
        {
            var job = new Job
            {
                Name = "Advanced Job",
                VerificationMode = VerificationMode.SHA256,
                BandwidthLimit = new BandwidthLimit
                {
                    Enabled = true,
                    MegabytesPerSecond = 50.5
                },
                RetryPolicy = new RetryPolicy
                {
                    MaxAttempts = 5,
                    FailureBehavior = FailureBehavior.PauseJob
                }
            };

            string json = JsonSerializer.Serialize(job);
            var deserialized = JsonSerializer.Deserialize<Job>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(VerificationMode.SHA256, deserialized.VerificationMode);
            Assert.True(deserialized.BandwidthLimit.Enabled);
            Assert.Equal(50.5, deserialized.BandwidthLimit.MegabytesPerSecond);
            Assert.Equal(5, deserialized.RetryPolicy.MaxAttempts);
            Assert.Equal(FailureBehavior.PauseJob, deserialized.RetryPolicy.FailureBehavior);
        }

        [Fact]
        public async Task SettingsRepository_LanguagePersistence_SavesAndLoadsLanguagePreference()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SettingsLangTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                var repo = new SettingsRepository(tempDir);
                var settings = await repo.GetAsync();
                Assert.Equal("tr-TR", settings.Language);

                settings.Language = "en-US";
                await repo.SaveAsync(settings);

                var reloaded = await repo.GetAsync();
                Assert.Equal("en-US", reloaded.Language);
            }
            finally
            {
                if (System.IO.Directory.Exists(tempDir))
                {
                    try { System.IO.Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void LocalizationService_ResourceAvailability_ForTurkish_ReturnsTurkishStrings()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("tr-TR");

            Assert.Equal("tr-TR", loc.CurrentLanguage);
            Assert.Equal("Duraklat", loc.GetString("Pause"));
            Assert.Equal("Devam Et", loc.GetString("Resume"));
            Assert.Equal("İptal", loc.GetString("Cancel"));
            Assert.Equal("Durdur", loc.GetString("Stop"));
            Assert.Equal("Yeniden Dene", loc.GetString("Retry"));
            Assert.Equal("Başarısız", loc.GetString("Failed"));
            Assert.Equal("Tamamlandı", loc.GetString("Completed"));
            Assert.Equal("Kopyalanıyor", loc.GetString("Copying"));
            Assert.Equal("Kalan Süre", loc.GetString("RemainingTime"));
            Assert.Equal("Aktarım Hızı", loc.GetString("TransferSpeed"));
            Assert.Equal("Doğrulama", loc.GetString("Verification"));
            Assert.Equal("Bant Genişliği Sınırı", loc.GetString("BandwidthLimit"));
        }

        [Fact]
        public void LocalizationService_ResourceAvailability_ForEnglish_ReturnsEnglishStrings()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("en-US");

            Assert.Equal("en-US", loc.CurrentLanguage);
            Assert.Equal("Pause", loc.GetString("Pause"));
            Assert.Equal("Resume", loc.GetString("Resume"));
            Assert.Equal("Cancel", loc.GetString("Cancel"));
            Assert.Equal("Stop", loc.GetString("Stop"));
            Assert.Equal("Retry", loc.GetString("Retry"));
            Assert.Equal("Failed", loc.GetString("Failed"));
            Assert.Equal("Completed", loc.GetString("Completed"));
            Assert.Equal("Copying", loc.GetString("Copying"));
            Assert.Equal("Remaining Time", loc.GetString("RemainingTime"));
            Assert.Equal("Transfer Speed", loc.GetString("TransferSpeed"));
            Assert.Equal("Verification", loc.GetString("Verification"));
            Assert.Equal("Bandwidth Limit", loc.GetString("BandwidthLimit"));
        }

        [Fact]
        public void LocalizationService_UnknownLanguageFallback_FallsBackToDefaultTurkish()
        {
            var loc = new LocalizationService();
            loc.SetLanguage("de-DE"); // Unknown / unsupported language

            Assert.Equal("tr-TR", loc.CurrentLanguage);
            Assert.Equal("Duraklat", loc.GetString("Pause"));
            Assert.Equal("Devam Et", loc.GetString("Resume"));
        }
    }
}
