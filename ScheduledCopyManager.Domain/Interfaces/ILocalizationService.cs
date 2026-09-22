using System;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        void SetLanguage(string cultureCode);
        string GetString(string key, string? defaultValue = null);
        event EventHandler LanguageChanged;
    }
}
