namespace MinecraftServerManager.Services;

public interface ICurseForgeApiKeyService
{
    string? GetApiKey();

    bool HasStoredOverride();

    Task<CurseForgeApiKeyStatus> ValidateStoredAsync(
        CancellationToken cancellationToken = default);

    Task<CurseForgeApiKeyStatus> ValidateAndStoreAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    void Remove();
}

public sealed record CurseForgeApiKeyStatus(
    bool HasStoredKey,
    bool IsValid,
    string Message)
{
    public bool UsesApplicationKey { get; init; }

    public bool PersonalKeyWasStored { get; init; }
}
