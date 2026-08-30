using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerProfileDuplicateService
{
    Task<ServerProfileDuplicateResult> DuplicateAsync(
        ServerProfileDuplicateRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
