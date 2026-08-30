using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackDependencyResolver
{
    Task<PackResolutionPlan> ResolveAsync(
        PackResolveRequest request,
        CancellationToken cancellationToken = default);
}
