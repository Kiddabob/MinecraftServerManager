using System.Security.Cryptography;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class CurseForgePackContentDownloadProvider : IPackContentDownloadProvider
{
    private readonly HttpClient _httpClient;

    public CurseForgePackContentDownloadProvider()
        : this(PackContentDownloadUtilities.CreateHttpClient())
    {
    }

    internal CurseForgePackContentDownloadProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string ProviderId => "curseforge";

    public void ValidateFile(ServerContentFile file)
    {
        PackContentDownloadUtilities.ValidateCommonFile(file);
        if (!IsTrustedHost(file.DownloadUri.Host)
            || file.Sha1.Length != 40
            || !file.Sha1.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{file.FileName} does not have trusted CurseForge download metadata.");
        }
    }

    public Task DownloadAndVerifyAsync(
        ServerContentFile file,
        string destinationPath,
        Action<long>? reportDownloadedBytes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFile(file);
        return PackContentDownloadUtilities.DownloadAndVerifyAsync(
            _httpClient,
            file,
            destinationPath,
            HashAlgorithmName.SHA1,
            file.Sha1,
            reportDownloadedBytes,
            cancellationToken);
    }

    private static bool IsTrustedHost(string host) =>
        host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
}
