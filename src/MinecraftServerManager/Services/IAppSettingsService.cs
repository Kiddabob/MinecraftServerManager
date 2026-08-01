using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IAppSettingsService
{
    AppPreferences Current { get; }

    Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}
