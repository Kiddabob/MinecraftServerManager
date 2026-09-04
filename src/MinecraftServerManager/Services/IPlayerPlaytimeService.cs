using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPlayerPlaytimeService
{
    event EventHandler? Changed;

    Task InitializeAsync(
        IEnumerable<ServerProfile> profiles,
        CancellationToken cancellationToken = default);

    void RecordConnection(string profileId, PlayerConnectionChange change);

    void UpdateProfileDisplayName(string profileId, string displayName);

    void CloseSessions(string profileId);

    IReadOnlyList<PlayerPlaytimeSnapshot> GetSnapshots();

    Task FlushAsync(CancellationToken cancellationToken = default);
}
