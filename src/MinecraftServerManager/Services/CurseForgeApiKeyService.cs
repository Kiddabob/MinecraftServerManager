using System.Net;
using System.Net.Http.Headers;

namespace MinecraftServerManager.Services;

public sealed class CurseForgeApiKeyService : ICurseForgeApiKeyService
{
    private const int MaximumApiKeyLength = 1024;
    private readonly ICurseForgeApiKeyStore _store;
    private readonly HttpClient _httpClient;

    public CurseForgeApiKeyService(ICurseForgeApiKeyStore store)
        : this(store, CreateHttpClient())
    {
    }

    internal CurseForgeApiKeyService(ICurseForgeApiKeyStore store, HttpClient httpClient)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string? GetApiKey()
    {
        var value = _store.Read()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<CurseForgeApiKeyStatus> ValidateStoredAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (apiKey is null)
        {
            return new CurseForgeApiKeyStatus(
                false,
                false,
                "No approved CurseForge developer key is stored on this Windows account.");
        }

        var isValid = await ValidateAsync(apiKey, cancellationToken);
        return new CurseForgeApiKeyStatus(
            true,
            isValid,
            isValid
                ? "Connected with an approved developer key stored securely for this Windows account."
                : "CurseForge rejected the stored key. Replace it with a currently approved developer key or remove it.");
    }

    public async Task<CurseForgeApiKeyStatus> ValidateAndStoreAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(apiKey);
        if (!await ValidateAsync(normalized, cancellationToken))
        {
            var existingKeyRemainsStored = GetApiKey() is not null;
            return new CurseForgeApiKeyStatus(
                existingKeyRemainsStored,
                false,
                existingKeyRemainsStored
                    ? "CurseForge rejected that replacement key. The previously stored key was left unchanged."
                    : "CurseForge rejected that key. Nothing was stored.");
        }

        _store.Save(normalized);
        return new CurseForgeApiKeyStatus(
            true,
            true,
            "Connected. The approved key is stored in Windows Credential Manager and is available only to this Windows account.");
    }

    public void Remove() => _store.Remove();

    private async Task<bool> ValidateAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/games/432");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        if ((int)response.StatusCode == 429)
        {
            throw new HttpRequestException("CurseForge could not validate the key because its API quota is currently limited.");
        }

        throw new HttpRequestException(
            $"CurseForge key validation failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown status"}).");
    }

    private static string Normalize(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var normalized = apiKey.Trim();
        if (normalized.Length > MaximumApiKeyLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The CurseForge API key format is not valid.", nameof(apiKey));
        }

        return normalized;
    }

    private static HttpClient CreateHttpClient() => new()
    {
        BaseAddress = new Uri("https://api.curseforge.com/"),
        Timeout = TimeSpan.FromSeconds(20)
    };
}
