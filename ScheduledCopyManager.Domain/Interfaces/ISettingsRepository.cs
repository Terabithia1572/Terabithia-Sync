using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Domain.Interfaces;

/// <summary>
/// Repository for persisting Settings.
/// </summary>
public interface ISettingsRepository
{
    Task<Settings> GetAsync();
    Task SaveAsync(Settings settings);
}
