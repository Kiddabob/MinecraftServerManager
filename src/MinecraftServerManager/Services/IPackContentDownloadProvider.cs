using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackContentDownloadProvider
{
    string ProviderId { get; }

    void ValidateFile(ServerContentFile file);

    Task DownloadAndVerifyAsync(
        ServerContentFile file,
        string destinationPath,
        Action<long>? reportDownloadedBytes = null,
        CancellationToken cancellationToken = default);
}
