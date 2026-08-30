using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackDraftOutputService
{
    Task<PackOutputPlan> CreatePlanAsync(
        PackOutputRequest request,
        CancellationToken cancellationToken = default);

    Task<PackOutputResult> CreateOutputAsync(
        PackOutputPlan plan,
        IProgress<PackOutputProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
