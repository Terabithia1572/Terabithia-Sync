using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IPreflightValidationService
    {
        Task<PreflightResult> ValidateJobAsync(Job job);
    }
}
