using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class WelcomeViewModel : ObservableObject
    {
        private readonly ISettingsRepository _settingsRepository;

        public event Action? RequestClose;

        public WelcomeViewModel(ISettingsRepository settingsRepository)
        {
            _settingsRepository = settingsRepository;
        }

        [RelayCommand]
        public async Task GetStartedAsync()
        {
            var settings = await _settingsRepository.GetAsync();
            settings.IsFirstRun = false;
            await _settingsRepository.SaveAsync(settings);
            RequestClose?.Invoke();
        }
    }
}
