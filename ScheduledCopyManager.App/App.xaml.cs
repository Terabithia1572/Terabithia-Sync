using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.DependencyInjection;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;

namespace ScheduledCopyManager.App
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;
        private IHost? _host;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        public App()
        {
            // Register Global Unhandled Exception Handlers
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Infrastructure
                    services.AddInfrastructureServices();

                    // Presentation Services
                    services.AddSingleton<IDialogService, DialogService>();
                    services.AddSingleton<ILocalizationService, LocalizationService>();

                    // ViewModels
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<JobsViewModel>();
                    services.AddSingleton<HistoryViewModel>();
                    services.AddSingleton<LogsViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<AboutViewModel>();
                    services.AddSingleton<ToolsViewModel>();
                    services.AddSingleton<WelcomeViewModel>();

                    // Main Window
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogAndShowGlobalError("UI İş parçacığı hatası", e.Exception);
            e.Handled = true; // Prevent application crash
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogAndShowGlobalError("Uygulama etki alanı hatası", ex);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogAndShowGlobalError("Arka plan görev hatası", e.Exception);
            e.SetObserved(); // Prevent process termination
        }

        private void LogAndShowGlobalError(string contextTitle, Exception ex)
        {
            try
            {
                var logService = _host?.Services.GetService<ILogService>();
                logService?.LogError($"Beklenmeyen Hata ({contextTitle}): {ex.Message}", ex);

                System.Windows.MessageBox.Show(
                    "Beklenmeyen bir hata oluştu. İşlem tamamlanamadı. Ayrıntılar günlük dosyasına kaydedildi.",
                    "Terabithia Sync - Hata",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch
            {
                // Fallback
                System.Windows.MessageBox.Show(
                    "Beklenmeyen bir hata oluştu. Ayrıntılar günlük dosyasına kaydedildi.",
                    "Terabithia Sync - Hata",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            const string MutexName = "Global\\TerabithiaSync_SingleInstance_Mutex";
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "Terabithia Sync zaten arka planda veya sistem tepsisinde çalışıyor.",
                    "Terabithia Sync",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                Shutdown();
                return;
            }

            await _host!.StartAsync();

            var logService = _host.Services.GetRequiredService<ILogService>();
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;
            int tid = Environment.CurrentManagedThreadId;

            logService.LogInformation($"[BUILD IDENTITY]\nBuildId={BuildInfo.BuildId}\nExecutablePath={AppDomain.CurrentDomain.BaseDirectory}\nProcessId={pid}\nProcessStartTime={nowMs}\nBuildIdentifier={BuildInfo.BuildIdentifier}");
            logService.LogInformation($"[PROCESS TRACE] Event=App.OnStartup PID={pid} TID={tid} Timestamp='{nowMs}' BuildId='{BuildInfo.BuildId}' MutexAcquired={createdNew}");

            var mainVm = _host.Services.GetRequiredService<MainViewModel>();
            await mainVm.InitializeAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = mainVm;

            // System Tray Setup
            SetupSystemTray(mainVm, mainWindow, logService);

            var settingsRepo = _host.Services.GetRequiredService<ISettingsRepository>();
            var settings = await settingsRepo.GetAsync();

            var localizationService = _host.Services.GetService<ILocalizationService>();
            localizationService?.SetLanguage(settings.Language);

            bool startMinimized = settings.StartMinimized || (e.Args.Length > 0 && e.Args[0] == "--autostart");
            if (!startMinimized)
            {
                mainWindow.Show();
            }
        }

        private void SetupSystemTray(MainViewModel mainVm, MainWindow mainWindow, ILogService logService)
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Terabithia Sync",
                Visible = true
            };

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new Icon(iconPath);
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            // Context Menu in Turkish
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            contextMenu.Items.Add("Terabithia Sync'i Aç", null, (s, e) =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            });

            contextMenu.Items.Add("Aktif Görevler", null, async (s, e) =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                await mainVm.NavigateToJobsAsync();
            });

            contextMenu.Items.Add("-");

            contextMenu.Items.Add("Çıkış", null, async (s, e) =>
            {
                logService.LogInformation($"[EXECUTION TRACE] [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID:{Environment.ProcessId}] User clicked Exit from tray icon.");
                await mainVm.ExitAppAsync();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.DoubleClick += (s, e) =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            };

            mainVm.RequestShowWindow += () =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            };

            mainVm.RequestCloseApp += () =>
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                Shutdown();
            };
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            string nowMs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int pid = Environment.ProcessId;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            if (_host != null)
            {
                var logService = _host.Services.GetService<ILogService>();
                logService?.LogInformation($"[EXECUTION TRACE] [{nowMs}] [PID:{pid}] App.OnExit: Application shutting down.");

                var scheduler = _host.Services.GetService<IJobScheduler>();
                if (scheduler != null)
                {
                    await scheduler.ShutdownAsync();
                }
                await _host.StopAsync();
                _host.Dispose();
            }

            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
