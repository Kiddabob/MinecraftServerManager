using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ManagedJavaRuntimeService : IManagedJavaRuntimeService
{
    private static readonly int[] SupportedMajors = [8, 16, 17, 21];
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public ManagedJavaRuntimeService(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    internal static string RuntimesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "Runtimes");

    public IReadOnlyList<ManagedJavaRuntimeOption> GetOptions() =>
    [
        CreateOption(8, "Minecraft 1.16.5 and older", isLongTermSupported: true),
        CreateOption(16, "Minecraft 1.17 only", isLongTermSupported: false),
        CreateOption(17, "Minecraft 1.18 to 1.20.4", isLongTermSupported: true),
        CreateOption(21, "Minecraft 1.20.5 and newer", isLongTermSupported: true)
    ];

    public async Task<JavaRuntimeInfo> InstallAsync(
        int majorVersion,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedMajors.Contains(majorVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(majorVersion),
                $"Java {majorVersion} is not a managed runtime option.");
        }

        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var targetDirectory = GetRuntimeDirectory(majorVersion);
            var existingExecutable = FindJavaExecutable(targetDirectory);
            if (existingExecutable is not null)
            {
                try
                {
                    return await DiscoverInstalledRuntimeAsync(existingExecutable, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    TryDeleteDirectory(targetDirectory);
                }
            }

            var asset = await GetLatestAssetAsync(majorVersion, cancellationToken);
            Directory.CreateDirectory(RuntimesRoot);
            var operationId = Guid.NewGuid().ToString("N");
            var archivePath = Path.Combine(RuntimesRoot, $"download-{majorVersion}-{operationId}.zip");
            var extractionDirectory = Path.Combine(RuntimesRoot, $"extract-{majorVersion}-{operationId}");
            try
            {
                await DownloadAsync(asset.DownloadUri, archivePath, cancellationToken);
                await VerifyChecksumAsync(archivePath, asset.Sha256, cancellationToken);
                Directory.CreateDirectory(extractionDirectory);
                ZipFile.ExtractToDirectory(archivePath, extractionDirectory);

                var extractedExecutable = FindJavaExecutable(extractionDirectory)
                    ?? throw new InvalidDataException("The downloaded Java archive did not contain bin\\java.exe.");
                var javaHome = Directory.GetParent(Directory.GetParent(extractedExecutable)!.FullName)!.FullName;
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, recursive: true);
                }

                Directory.Move(javaHome, targetDirectory);
                var installedExecutable = FindJavaExecutable(targetDirectory)
                    ?? throw new InvalidDataException("Java was extracted but its executable could not be found.");
                try
                {
                    return await DiscoverInstalledRuntimeAsync(installedExecutable, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or OperationCanceledException)
                {
                    TryDeleteDirectory(targetDirectory);
                    throw;
                }
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(extractionDirectory);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    internal static string GetRuntimeDirectory(int majorVersion) =>
        Path.Combine(RuntimesRoot, $"temurin-{majorVersion}");

    private ManagedJavaRuntimeOption CreateOption(
        int majorVersion,
        string minecraftVersions,
        bool isLongTermSupported)
    {
        var executable = FindJavaExecutable(GetRuntimeDirectory(majorVersion)) ?? string.Empty;
        return new ManagedJavaRuntimeOption(
            majorVersion,
            minecraftVersions,
            isLongTermSupported,
            executable.Length > 0,
            executable);
    }

    private async Task<JavaRuntimeInfo> DiscoverInstalledRuntimeAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var runtimes = await _javaRuntimeService.DiscoverAsync([executable], cancellationToken);
        return runtimes.FirstOrDefault(runtime =>
                string.Equals(runtime.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The managed Java runtime was installed but could not be started.");
    }

    private static async Task<AdoptiumAsset> GetLatestAssetAsync(
        int majorVersion,
        CancellationToken cancellationToken)
    {
        var imageType = majorVersion == 16 ? "jdk" : "jre";
        var endpoint = majorVersion == 16
            ? "https://api.adoptium.net/v3/assets/feature_releases/16/ga?architecture=x64&heap_size=normal&image_type=jdk&jvm_impl=hotspot&os=windows&page=0&page_size=1&project=jdk&sort_method=DATE&sort_order=DESC"
            : $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?architecture=x64&heap_size=normal&image_type={imageType}&os=windows&project=jdk";
        using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Adoptium did not return a Java {majorVersion} Windows package.");
        }

        string? link;
        string? checksum;
        try
        {
            var binary = document.RootElement[0].GetProperty("binaries")[0];
            var package = binary.GetProperty("package");
            link = package.GetProperty("link").GetString();
            checksum = package.GetProperty("checksum").GetString();
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Adoptium returned incomplete Java package metadata.", exception);
        }
        if (!Uri.TryCreate(link, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || checksum is null
            || checksum.Length != 64
            || !checksum.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Adoptium returned invalid download metadata.");
        }

        return new AdoptiumAsset(downloadUri, checksum);
    }

    private static async Task DownloadAsync(
        Uri downloadUri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task VerifyChecksumAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Java download failed its SHA-256 integrity check.");
        }
    }

    private static string? FindJavaExecutable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "java.exe", SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    new DirectoryInfo(Path.GetDirectoryName(path)!).Name,
                    "bin",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MinecraftServerManager", "0.1"));
        return client;
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
            // A future install can safely ignore an abandoned uniquely named download.
        }
        catch (UnauthorizedAccessException)
        {
            // A future install can safely ignore an abandoned uniquely named download.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A future install can safely ignore an abandoned uniquely named extraction.
        }
        catch (UnauthorizedAccessException)
        {
            // A future install can safely ignore an abandoned uniquely named extraction.
        }
    }

    private sealed record AdoptiumAsset(Uri DownloadUri, string Sha256);
}
