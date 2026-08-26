using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IProfileService
{
    Task<ServerProfile> LoadAsync(string fileName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerProfile>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default);

    Task<ProfileImportResult> ImportFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default);
}
