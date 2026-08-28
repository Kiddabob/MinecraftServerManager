using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class VanillaServerBaselineInstaller : IServerBaselineInstaller
{
    private const int MaximumVersionMetadataBytes = 5 * 1024 * 1024;
    private const long MaximumServerJarBytes = 256L * 1024 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly Uri VersionManifestUri = new(
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public bool CanInstall(string loaderId) =>
        loaderId.Equals("minecraft", StringComparison.OrdinalIgnoreCase);

    public async Task<ServerBaselineInstallResult> InstallAsync(
        ServerBaselineInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanInstall(request.LoaderId))
        {
            throw new NotSupportedException($"This installer does not support '{request.LoaderId}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        var serverDirectory = Path.GetFullPath(request.ServerDirectory);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException($"The staged server folder does not exist: {serverDirectory}");
        }

        var launcherFileName = $"minecraft_server.{request.MinecraftVersion}.jar";
        var launcherPath = Path.Combine(serverDirectory, launcherFileName);
        if (File.Exists(launcherPath))
        {
            ValidateServerJar(launcherPath);
            return new ServerBaselineInstallResult(
                false,
                launcherFileName,
                "The pack already supplied a valid Vanilla Minecraft server launcher.");
        }

        progress?.Report($"Finding the official Minecraft {request.MinecraftVersion} server download…");
        using var manifestResponse = await HttpClient.GetAsync(VersionManifestUri, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        EnsureOfficialUri(manifestResponse.RequestMessage?.RequestUri, "piston-meta.mojang.com");
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var manifestDocument = await JsonDocument.ParseAsync(
            manifestStream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
        var metadataReference = ParseVersionMetadataReference(
            manifestDocument.RootElement,
            request.MinecraftVersion);

        var metadataBytes = await DownloadSmallVerifiedFileAsync(
            metadataReference.Uri,
            metadataReference.Sha1,
            MaximumVersionMetadataBytes,
            "piston-meta.mojang.com",
            cancellationToken);
        using var metadataDocument = JsonDocument.Parse(
            metadataBytes,
            new JsonDocumentOptions { MaxDepth = 64 });
        var serverDownload = ParseServerDownload(metadataDocument.RootElement);

        progress?.Report($"Downloading the official Minecraft {request.MinecraftVersion} server JAR…");
        try
        {
            await DownloadServerJarAsync(
                serverDownload,
                launcherPath,
                cancellationToken);
            ValidateServerJar(launcherPath);
            return new ServerBaselineInstallResult(
                true,
                launcherFileName,
                $"Installed the official Minecraft {request.MinecraftVersion} server launcher.");
        }
        catch
        {
            TryDeleteFile(launcherPath);
            throw;
        }
    }

    internal static MojangVersionMetadataReference ParseVersionMetadataReference(
        JsonElement root,
        string minecraftVersion)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Mojang returned invalid Minecraft version metadata.");
        }

        foreach (var version in versions.EnumerateArray())
        {
            if (!GetString(version, "id").Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var urlText = GetString(version, "url");
            var sha1 = GetString(version, "sha1");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase)
                || !IsSha1(sha1))
            {
                throw new InvalidDataException("Mojang returned unsafe version metadata.");
            }

            return new MojangVersionMetadataReference(uri, sha1);
        }

        throw new InvalidDataException($"Mojang does not publish a server for Minecraft {minecraftVersion}.");
    }

    internal static MojangServerDownload ParseServerDownload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("downloads", out var downloads)
            || downloads.ValueKind != JsonValueKind.Object
            || !downloads.TryGetProperty("server", out var server)
            || server.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("This Minecraft version has no official dedicated-server download.");
        }

        var urlText = GetString(server, "url");
        var sha1 = GetString(server, "sha1");
        if (!server.TryGetProperty("size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size is <= 0 or > MaximumServerJarBytes
            || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsApprovedServerHost(uri.Host)
            || !IsSha1(sha1))
        {
            throw new InvalidDataException("Mojang returned unsafe server-download metadata.");
        }

        return new MojangServerDownload(uri, sha1, size);
    }

    internal static void ValidateServerJar(string launcherPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(launcherPath);
            var manifest = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
            if (manifest is null || manifest.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("The Minecraft server JAR has no valid manifest.");
            }

            using var reader = new StreamReader(manifest.Open());
            var manifestText = reader.ReadToEnd();
            if (!manifestText.Contains("Main-Class:", StringComparison.OrdinalIgnoreCase)
                || !manifestText.Contains("minecraft", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded JAR is not a Minecraft server launcher.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The Minecraft server JAR could not be validated.", exception);
        }
    }

    private static async Task<byte[]> DownloadSmallVerifiedFileAsync(
        Uri uri,
        string expectedSha1,
        int maximumBytes,
        string expectedHost,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureOfficialUri(response.RequestMessage?.RequestUri, expectedHost);
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The Minecraft version metadata exceeds the safe limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The Minecraft version metadata exceeds the safe limit.");
            }

            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Minecraft version metadata failed SHA-1 verification.");
        }

        return destination.ToArray();
    }

    private static async Task DownloadServerJarAsync(
        MojangServerDownload download,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            download.Uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null
            || finalUri.Scheme != Uri.UriSchemeHttps
            || !IsApprovedServerHost(finalUri.Host))
        {
            throw new InvalidDataException("Mojang redirected the server download to an unexpected host.");
        }

        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength != download.Size)
        {
            throw new InvalidDataException("The Minecraft server download size does not match its metadata.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920];
        long completed = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            completed = checked(completed + read);
            if (completed > download.Size)
            {
                throw new InvalidDataException("The Minecraft server download exceeds its published size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        if (completed != download.Size)
        {
            throw new InvalidDataException("The Minecraft server download is incomplete.");
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(download.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Minecraft server JAR failed Mojang's SHA-1 verification.");
        }
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsApprovedServerHost(string host) =>
        host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha1(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static void EnsureOfficialUri(Uri? uri, string expectedHost)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mojang redirected metadata to an unexpected host.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }
}

internal sealed record MojangVersionMetadataReference(Uri Uri, string Sha1);

internal sealed record MojangServerDownload(Uri Uri, string Sha1, long Size);
