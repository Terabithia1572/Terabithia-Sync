using System.Collections.Generic;
using System.Threading.Tasks;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public interface IPathValidationService
    {
        Task<IReadOnlyList<string>> ValidateSourcesAsync(IEnumerable<string> sourcePaths);
        Task<IReadOnlyList<string>> ValidateDestinationAsync(string destinationPath);
        Task<IReadOnlyList<string>> ValidateJobPathsAsync(IEnumerable<string> sourcePaths, string destinationPath);
    }
}
