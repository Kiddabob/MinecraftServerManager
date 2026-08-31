namespace MinecraftServerManager.Services;

public interface IPlayerAvatarService
{
    Task<string?> GetAvatarPathAsync(
        string playerName,
        Guid? playerId = null,
        CancellationToken cancellationToken = default);
}
