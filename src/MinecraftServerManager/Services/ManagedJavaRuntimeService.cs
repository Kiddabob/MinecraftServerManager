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
        IProgress<ManagedJavaInstallProgress>? progress = null,
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
                    progress?.Report(new(
                        ManagedJavaInstallStage.Validating,
                        $"Checking the existing Java {majorVersion} runtime…"));
                    return await DiscoverInstalledRuntimeAsync(existingExecutable, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    TryDeleteDirectory(targetDirectory);
                }
            }

            progress?.Report(new(
                ManagedJavaInstallStage.Checking,
                $"Finding the latest compatible Java {majorVersion} package…"));
            var asset = await GetLatestAssetAsync(majorVersion, cancellationToken);
            Directory.CreateDirectory(RuntimesRoot);
            var operationId = Guid.NewGuid().ToString("N");
            var archivePath = Path.Combine(RuntimesRoot, $"download-{majorVersion}-{operationId}.zip");
            var extractionDirectory = Path.Combine(RuntimesRoot, $"extract-{majorVersion}-{operationId}");
            try
            {
                await DownloadAsync(
                    majorVersion,
                    asset,
                    archivePath,
                    progress,
                    cancellationToken);
                await VerifyChecksumAsync(
                    majorVersion,
                    archivePath,
                    asset.Sha256,
                    progress,
                    cancellationToken);
                progress?.Report(new(
                    ManagedJavaInstallStage.Extracting,
                    $"Extracting Java {majorVersion}…"));
                Directory.CreateDirectory(extractionDirectory);
                ZipFile.ExtractToDirectory(archivePath, extractionDirectory);

                var extractedExecutable = FindJavaExecutable(extractionDirectory)
                    ?? throw new InvalidDataException("The downloaded Java archive did not contain bin\\java.exe.");
                var javaHome = Directory.GetParent(Directory.GetParent(extractedExecutable)!.FullName)!.FullName;
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, recursive: true);
                }

                progress?.Report(new(
                    ManagedJavaInstallStage.Installing,
                    $"Installing Java {majorVersion} for Minecraft Server Manager…"));
                Directory.Move(javaHome, targetDirectory);
                var installedExecutable = FindJavaExecutable(targetDirectory)
                    ?? throw new InvalidDataException("Java was extracted but its executable could not be found.");
                try
                {
                    progress?.Report(new(
                        ManagedJavaInstallStage.Validating,
                        $"Starting Java {majorVersion} once to verify the installation…"));
                    var runtime = await DiscoverInstalledRuntimeAsync(installedExecutable, cancellationToken);
                    progress?.Report(new(
                        ManagedJavaInstallStage.Completed,
                        $"Java {majorVersion} is installed and ready.",
                        asset.Size,
                        asset.Size));
                    return runtime;
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

        return ParseAssetMetadata(document.RootElement, majorVersion, imageType);
    }

    internal static AdoptiumAsset ParseAssetMetadata(
        JsonElement root,
        int majorVersion,
        string? expectedImageType = null)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Adoptium returned invalid Java package metadata.");
        }

        expectedImageType ??= majorVersion == 16 ? "jdk" : "jre";
        foreach (var release in root.EnumerateArray())
        {
            foreach (var binary in EnumerateBinaries(release))
            {
                if (!MatchesBinary(binary, expectedImageType)
                    || !binary.TryGetProperty("package", out var package)
                    || !TryReadPackage(package, out var asset))
                {
                    continue;
                }

                return asset;
            }
        }

        throw new InvalidDataException(
            $"Adoptium did not return a complete Java {majorVersion} Windows x64 ZIP package.");
    }

    private static IEnumerable<JsonElement> EnumerateBinaries(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (release.TryGetProperty("binary", out var binary)
            && binary.ValueKind == JsonValueKind.Object)
        {
            yield return binary;
        }

        if (!release.TryGetProperty("binaries", out var binaries)
            || binaries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var arrayBinary in binaries.EnumerateArray())
        {
            if (arrayBinary.ValueKind == JsonValueKind.Object)
            {
                yield return arrayBinary;
            }
        }
    }

    private static bool MatchesBinary(JsonElement binary, string expectedImageType) =>
        HasStringValue(binary, "architecture", "x64")
        && HasStringValue(binary, "os", "windows")
        && HasStringValue(binary, "image_type", expectedImageType);

    private static bool HasStringValue(JsonElement element, string propertyName, string expected) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadPackage(JsonElement package, out AdoptiumAsset asset)
    {
        asset = default!;
        if (package.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var link = package.TryGetProperty("link", out var linkElement)
            && linkElement.ValueKind == JsonValueKind.String
            ? linkElement.GetString()
            : null;
        var checksum = package.TryGetProperty("checksum", out var checksumElement)
            && checksumElement.ValueKind == JsonValueKind.String
            ? checksumElement.GetString()
            : null;
        var size = package.TryGetProperty("size", out var sizeElement)
            && sizeElement.TryGetInt64(out var packageSize)
                ? packageSize
                : (long?)null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !downloadUri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || checksum is null
            || checksum.Length != 64
            || !checksum.All(Uri.IsHexDigit))
        {
            return false;
        }

        asset = new AdoptiumAsset(downloadUri, checksum, size);
        return true;
    }

    private static async Task DownloadAsync(
        int majorVersion,
        AdoptiumAsset asset,
        string destinationPath,
        IProgress<ManagedJavaInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            asset.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long completedBytes = 0;
        var lastReportedPercent = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            completedBytes += read;
            lastReportedPercent = ReportByteProgress(
                ManagedJavaInstallStage.Downloading,
                majorVersion,
                "Downloading",
                completedBytes,
                totalBytes,
                lastReportedPercent,
                progress);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task VerifyChecksumAsync(
        int majorVersion,
        string path,
        string expectedSha256,
        IProgress<ManagedJavaInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long completedBytes = 0;
        var lastReportedPercent = -1;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            completedBytes += read;
            lastReportedPercent = ReportByteProgress(
                ManagedJavaInstallStage.Verifying,
                majorVersion,
                "Verifying",
                completedBytes,
                stream.Length,
                lastReportedPercent,
                progress);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Java download failed its SHA-256 integrity check.");
        }
    }

    private static int ReportByteProgress(
        ManagedJavaInstallStage stage,
        int majorVersion,
        string action,
        long completedBytes,
        long? totalBytes,
        int lastReportedPercent,
        IProgress<ManagedJavaInstallProgress>? progress)
    {
        if (progress is null)
        {
            return lastReportedPercent;
        }

        var percent = totalBytes is > 0
            ? (int)Math.Clamp(completedBytes * 100L / totalBytes.Value, 0L, 100L)
            : -1;
        if (percent >= 0 && percent == lastReportedPercent)
        {
            return lastReportedPercent;
        }

        var completedMegabytes = completedBytes / 1024d / 1024d;
        var message = totalBytes is > 0
            ? $"{action} Java {majorVersion}… {percent}% ({completedMegabytes:0.0} of {totalBytes.Value / 1024d / 1024d:0.0} MB)"
            : $"{action} Java {majorVersion}… {completedMegabytes:0.0} MB";
        progress.Report(new(stage, message, completedBytes, totalBytes));
        return percent;
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

    internal sealed record AdoptiumAsset(Uri DownloadUri, string Sha256, long? Size);
}
