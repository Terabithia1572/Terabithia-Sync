using Microsoft.Extensions.DependencyInjection;
using Quartz.Spi;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Scheduler;
using ScheduledCopyManager.Infrastructure.Services;

namespace ScheduledCopyManager.Infrastructure.DependencyInjection
{
    public static class InfrastructureServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, string? dataDirectory = null)
        {
            // Repositories
            services.AddSingleton<IJobRepository>(sp => new JobRepository(dataDirectory));
            services.AddSingleton<IHistoryRepository>(sp => new HistoryRepository(dataDirectory));
            services.AddSingleton<ISettingsRepository>(sp => new SettingsRepository(dataDirectory));
            services.AddSingleton<ICheckpointRepository>(sp => new CheckpointRepository(sp.GetService<ILogService>()));

            // Core Infrastructure Services
            services.AddSingleton<IJobExecutionGate, JobExecutionGate>();
            services.AddSingleton<IFileCopyService, FileCopyService>();
            services.AddSingleton<IPathValidationService, PathValidationService>();
            services.AddSingleton<IPreflightValidationService, PreflightValidationService>();
            services.AddSingleton<IUsbDriveService, UsbDriveService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<ILogService>(sp => new LogService(dataDirectory != null ? System.IO.Path.Combine(dataDirectory, "Logs") : null));
            services.AddSingleton<IStartupService, StartupService>();

            // Quartz Scheduler
            services.AddSingleton<IJobExecutionManager, JobExecutionManager>();
            services.AddSingleton<IJobFactory, CustomQuartzJobFactory>();
            services.AddSingleton<IJobScheduler, JobScheduler>();
            services.AddTransient<QuartzCopyJob>();

            return services;
        }
    }
}
