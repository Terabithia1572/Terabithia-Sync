using System;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Spi;

namespace ScheduledCopyManager.Infrastructure.Scheduler
{
    public class CustomQuartzJobFactory : IJobFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public CustomQuartzJobFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            return (IJob)_serviceProvider.GetRequiredService(bundle.JobDetail.JobType);
        }

        public void ReturnJob(IJob job)
        {
            (job as IDisposable)?.Dispose();
        }
    }
}
