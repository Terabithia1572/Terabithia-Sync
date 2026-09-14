using System.ComponentModel;
using System.Windows;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Presentation.ViewModels;

namespace ScheduledCopyManager.App
{
    public partial class MainWindow : Window
    {
        private readonly ISettingsRepository? _settingsRepository;

        public MainWindow(ISettingsRepository settingsRepository)
        {
            InitializeComponent();
            _settingsRepository = settingsRepository;
        }

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_settingsRepository != null)
            {
                var settings = await _settingsRepository.GetAsync();
                if (settings.CloseToTray && settings.EnableTrayIcon)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
            }

            base.OnClosing(e);
        }

        protected override async void OnStateChanged(System.EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _settingsRepository != null)
            {
                var settings = await _settingsRepository.GetAsync();
                if (settings.EnableTrayIcon)
                {
                    Hide();
                }
            }
            base.OnStateChanged(e);
        }
    }
}