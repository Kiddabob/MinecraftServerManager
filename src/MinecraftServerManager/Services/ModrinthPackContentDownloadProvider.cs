using System.Security.Cryptography;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModrinthPackContentDownloadProvider : IPackContentDownloadProvider
{
    private readonly HttpClient _httpClient;

    public ModrinthPackContentDownloadProvider()
        : this(PackContentDownloadUtilities.CreateHttpClient())
    {
    }

    internal ModrinthPackContentDownloadProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string ProviderId => "modrinth";

    public void ValidateFile(ServerContentFile file)
    {
        PackContentDownloadUtilities.ValidateCommonFile(file);
        if (!file.DownloadUri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
            || file.Sha512.Length != 128
            || !file.Sha512.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{file.FileName} does not have trusted Modrinth download metadata.");
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
            HashAlgorithmName.SHA512,
            file.Sha512,
            reportDownloadedBytes,
            cancellationToken);
    }
}
