namespace MinecraftServerManager.Services;

public interface ICurseForgeApiKeyStore
{
    string? Read();

    void Save(string apiKey);

    void Remove();
}
