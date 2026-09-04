using System.Net;
using System.Net.Http.Headers;

namespace MinecraftServerManager.Services;

public sealed class CurseForgeApiKeyService : ICurseForgeApiKeyService
{
    private const int MaximumApiKeyLength = 1024;
    private readonly ICurseForgeApiKeyStore _store;
    private readonly ICurseForgeApplicationApiKeyProvider _applicationKeyProvider;
    private readonly HttpClient _httpClient;

    public CurseForgeApiKeyService(
        ICurseForgeApiKeyStore store,
        ICurseForgeApplicationApiKeyProvider applicationKeyProvider)
        : this(store, applicationKeyProvider, CreateHttpClient())
    {
    }

    internal CurseForgeApiKeyService(ICurseForgeApiKeyStore store, HttpClient httpClient)
        : this(store, new EmptyApplicationApiKeyProvider(), httpClient)
    {
    }

    internal CurseForgeApiKeyService(
        ICurseForgeApiKeyStore store,
        ICurseForgeApplicationApiKeyProvider applicationKeyProvider,
        HttpClient httpClient)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applicationKeyProvider = applicationKeyProvider
            ?? throw new ArgumentNullException(nameof(applicationKeyProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string? GetApiKey()
    {
        var value = GetStoredOverride() ?? _applicationKeyProvider.GetApiKey()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public bool HasStoredOverride() => GetStoredOverride() is not null;

    public async Task<CurseForgeApiKeyStatus> ValidateStoredAsync(
        CancellationToken cancellationToken = default)
    {
        var storedOverride = GetStoredOverride();
        var applicationKey = _applicationKeyProvider.GetApiKey()?.Trim();
        var apiKey = storedOverride ?? applicationKey;
        if (apiKey is null)
        {
            return new CurseForgeApiKeyStatus(
                false,
                false,
                "No approved CurseForge developer key is stored on this Windows account.");
        }

        var isValid = await ValidateAsync(apiKey, cancellationToken);
        return new CurseForgeApiKeyStatus(
            storedOverride is not null,
            isValid,
            isValid
                ? storedOverride is not null
                    ? "Connected with a personal approved key stored securely for this Windows account."
                    : "Connected with the app-specific CurseForge key configured for this official build."
                : storedOverride is not null
                    ? "CurseForge rejected the personal key override. Replace or remove it to use any app-specific build access."
                    : "CurseForge rejected the app-specific key configured for this build.")
        {
            UsesApplicationKey = storedOverride is null && applicationKey is not null
        };
    }

    public async Task<CurseForgeApiKeyStatus> ValidateAndStoreAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(apiKey);
        if (!await ValidateAsync(normalized, cancellationToken))
        {
            var existingKeyRemainsStored = GetStoredOverride() is not null;
            var applicationKeyRemainsAvailable = !existingKeyRemainsStored
                && !string.IsNullOrWhiteSpace(_applicationKeyProvider.GetApiKey());
            return new CurseForgeApiKeyStatus(
                existingKeyRemainsStored,
                applicationKeyRemainsAvailable,
                existingKeyRemainsStored
                    ? "CurseForge rejected that replacement key. The previously stored key was left unchanged."
                    : applicationKeyRemainsAvailable
                        ? "CurseForge rejected that personal key. Nothing was stored, and this build's app-specific access remains active."
                        : "CurseForge rejected that key. Nothing was stored.")
            {
                UsesApplicationKey = applicationKeyRemainsAvailable
            };
        }

        _store.Save(normalized);
        return new CurseForgeApiKeyStatus(
            true,
            true,
            "Connected. The approved key is stored in Windows Credential Manager and is available only to this Windows account.")
        {
            PersonalKeyWasStored = true
        };
    }

    public void Remove() => _store.Remove();

    private string? GetStoredOverride()
    {
        var value = _store.Read()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

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

    private sealed class EmptyApplicationApiKeyProvider : ICurseForgeApplicationApiKeyProvider
    {
        public string? GetApiKey() => null;
    }
}
