using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

internal static class PackContentDownloadUtilities
{
    internal const long MaximumFileBytes = 1024L * 1024L * 1024L;

    internal static void ValidateCommonFile(ServerContentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Size <= 0 || file.Size > MaximumFileBytes)
        {
            throw new InvalidDataException($"{file.FileName} has an invalid or unsafe download size.");
        }

        if (!file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(file.FileName).Equals(file.FileName, StringComparison.Ordinal)
            || file.DownloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{file.FileName} does not have safe download metadata.");
        }
    }

    internal static async Task DownloadAndVerifyAsync(
        HttpClient httpClient,
        ServerContentFile file,
        string destinationPath,
        HashAlgorithmName hashAlgorithm,
        string expectedHash,
        Action<long>? reportDownloadedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateCommonFile(file);
        if (string.IsNullOrWhiteSpace(expectedHash) || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{file.FileName} does not provide a supported verification hash.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != file.Size)
        {
            throw new InvalidDataException($"{file.FileName} has a different size than its provider declared.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(hashAlgorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long downloaded = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                downloaded += count;
                if (downloaded > file.Size || downloaded > MaximumFileBytes)
                {
                    throw new InvalidDataException($"{file.FileName} exceeded its declared size.");
                }

                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                reportDownloadedBytes?.Invoke(downloaded);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (downloaded != file.Size)
        {
            throw new InvalidDataException($"{file.FileName} did not match its declared size.");
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{file.FileName} failed {hashAlgorithm.Name} verification.");
        }
    }

    internal static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.2"));
        return client;
    }
}
