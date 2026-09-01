using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ManagedMinecraftLauncherService : IManagedMinecraftLauncherService
{
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 512L * 1024 * 1024;
    private const long MaximumClientBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 5000;
    private const int MaximumClientEntries = 20_000;
    private const int MaximumManifestBytes = 32 * 1024 * 1024;
    private const string LauncherExecutableName = "prismlauncher.exe";
    private const string PortableMarkerName = "portable.txt";
    private const string InstallMetadataName = "minecraft-server-manager-launcher.json";
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/PrismLauncher/PrismLauncher/releases/latest");
    private readonly HttpClient _httpClient;
    private readonly Architecture _architecture;
    private readonly Func<string, string?, bool> _openLauncher;

    public ManagedMinecraftLauncherService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kidda.MinecraftServerManager",
                "Launcher",
                "Prism"),
            CreateHttpClient(),
            RuntimeInformation.ProcessArchitecture,
            OpenLauncherProcess)
    {
    }

    internal ManagedMinecraftLauncherService(
        string launcherDirectory,
        HttpClient httpClient,
        Architecture architecture,
        Func<string, string?, bool>? openLauncher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        LauncherDirectory = Path.GetFullPath(launcherDirectory);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _architecture = architecture;
        _openLauncher = openLauncher ?? ((_, _) => true);
    }

    public string LauncherDirectory { get; }

    public bool IsInstalled =>
        File.Exists(Path.Combine(LauncherDirectory, LauncherExecutableName))
        && File.Exists(Path.Combine(LauncherDirectory, PortableMarkerName));

    public async Task<ManagedLauncherInstallResult> InstallPackAsync(
        ManagedLauncherInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clientDirectory = ValidateClientDirectory(request.ClientDirectory);
        var manifestPath = ValidateManifest(request.ManifestPath);
        ValidatePackIdentity(request);
        var loaderUid = ResolveLoaderUid(request.LoaderId);

        var launcher = await EnsureLauncherInstalledAsync(progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Creating a separate managed launcher instance…");
        var instancesDirectory = Path.Combine(LauncherDirectory, "instances");
        EnsureOrdinaryDirectory(instancesDirectory, createIfMissing: true);
        var instanceId = CreateUniqueInstanceId(instancesDirectory, request.PackName);
        var instanceDirectory = Path.Combine(instancesDirectory, instanceId);
        var stagingDirectory = Path.Combine(instancesDirectory, $".msm-instance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var minecraftDirectory = Path.Combine(stagingDirectory, "minecraft");
            Directory.CreateDirectory(minecraftDirectory);
            CopyClientDirectory(clientDirectory, minecraftDirectory, cancellationToken);
            await WriteInstanceFilesAsync(
                stagingDirectory,
                request,
                loaderUid,
                instanceId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, instanceDirectory);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }

        var manifestWarning = string.Empty;
        try
        {
            MarkManifestPlayable(
                manifestPath,
                instanceId,
                instanceDirectory,
                launcher.Version);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException)
        {
            manifestWarning = $" The pack is usable, but its audit manifest could not be updated: {exception.Message}";
        }

        progress?.Report("The client is ready in the managed launcher.");
        var installText = launcher.InstalledNow
            ? $"Verified and installed Prism Launcher {launcher.Version} in the manager's private launcher folder. "
            : $"Used the manager's installed Prism Launcher {launcher.Version}. ";
        return new ManagedLauncherInstallResult(
            instanceId,
            instanceDirectory,
            LauncherDirectory,
            launcher.Version,
            launcher.InstalledNow,
            clientDirectory,
            $"{installText}Added {request.PackName} as a separate instance. Your normal Minecraft Launcher and .minecraft folder were not changed.{manifestWarning}");
    }

    public bool TryOpenLauncher(string? instanceId, out string message)
    {
        var executablePath = Path.Combine(LauncherDirectory, LauncherExecutableName);
        if (!IsInstalled)
        {
            message = "Install the managed launcher from Build a pack before opening it.";
            return false;
        }

        try
        {
            if (!_openLauncher(executablePath, instanceId))
            {
                message = "Windows did not open the managed launcher.";
                return false;
            }

            message = string.IsNullOrWhiteSpace(instanceId)
                ? "Opened the manager-owned Minecraft launcher."
                : $"Opened managed launcher instance {instanceId}.";
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or FileNotFoundException or UnauthorizedAccessException)
        {
            message = $"The managed launcher could not be opened: {exception.Message}";
            return false;
        }
    }

    private async Task<LauncherInstallState> EnsureLauncherInstalledAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return new LauncherInstallState(ReadInstalledVersion(), false);
        }

        if (Directory.Exists(LauncherDirectory))
        {
            throw new InvalidDataException(
                $"The managed launcher folder is incomplete and was left untouched: {LauncherDirectory}. Move it aside, then retry the installation.");
        }

        var parentDirectory = Path.GetDirectoryName(LauncherDirectory)
            ?? throw new InvalidDataException("The managed launcher folder has no parent directory.");
        EnsureOrdinaryDirectory(parentDirectory, createIfMissing: true);
        progress?.Report("Checking Prism Launcher's official stable release…");
        var release = await GetLatestReleaseAsync(cancellationToken);
        var asset = SelectPortableAsset(release);
        var archivePath = Path.Combine(parentDirectory, $".prism-{Guid.NewGuid():N}.zip");
        var stagingDirectory = Path.Combine(parentDirectory, $".prism-install-{Guid.NewGuid():N}");
        try
        {
            await DownloadVerifiedArchiveAsync(asset, archivePath, progress, cancellationToken);
            progress?.Report($"Installing the verified Prism Launcher {release.TagName} portable files…");
            Directory.CreateDirectory(stagingDirectory);
            await ExtractArchiveSafelyAsync(archivePath, stagingDirectory, cancellationToken);
            ValidateExtractedLauncher(stagingDirectory);
            await WriteInstallMetadataAsync(
                stagingDirectory,
                release.TagName,
                asset,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, LauncherDirectory);
            return new LauncherInstallState(release.TagName, true);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureUri(response.RequestMessage?.RequestUri, "api.github.com");
        if (response.Content.Headers.ContentLength is > MaximumReleaseMetadataBytes)
        {
            throw new InvalidDataException("Prism Launcher release metadata exceeds the safe limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limitedMetadata = new MemoryStream();
        var metadataBuffer = new byte[16 * 1024];
        int metadataRead;
        while ((metadataRead = await stream.ReadAsync(metadataBuffer, cancellationToken)) > 0)
        {
            if (limitedMetadata.Length + metadataRead > MaximumReleaseMetadataBytes)
            {
                throw new InvalidDataException("Prism Launcher release metadata exceeds the safe limit.");
            }

            await limitedMetadata.WriteAsync(
                metadataBuffer.AsMemory(0, metadataRead),
                cancellationToken);
        }

        limitedMetadata.Position = 0;
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            limitedMetadata,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 },
            cancellationToken)
            ?? throw new InvalidDataException("GitHub returned empty Prism Launcher release metadata.");
        if (string.IsNullOrWhiteSpace(release.TagName)
            || release.Draft
            || release.Prerelease
            || release.Assets.Count == 0)
        {
            throw new InvalidDataException("GitHub returned invalid stable Prism Launcher release metadata.");
        }

        return release;
    }

    private GitHubReleaseAsset SelectPortableAsset(GitHubRelease release)
    {
        var expectedName = _architecture switch
        {
            Architecture.X64 => $"PrismLauncher-Windows-MSVC-Portable-{release.TagName}.zip",
            Architecture.Arm64 => $"PrismLauncher-Windows-MSVC-arm64-Portable-{release.TagName}.zip",
            _ => throw new PlatformNotSupportedException(
                $"The managed launcher is not available for {_architecture} Windows builds.")
        };
        var asset = release.Assets.SingleOrDefault(candidate =>
            candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Prism Launcher {release.TagName} does not publish the expected Windows portable asset for {_architecture}.");
        if (asset.Size is <= 0 or > MaximumArchiveBytes
            || !TryParseSha256Digest(asset.Digest, out _)
            || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !downloadUri.AbsolutePath.StartsWith(
                "/PrismLauncher/PrismLauncher/releases/download/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Prism Launcher portable asset metadata is incomplete or untrusted.");
        }

        return asset;
    }

    private async Task DownloadVerifiedArchiveAsync(
        GitHubReleaseAsset asset,
        string archivePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryParseSha256Digest(asset.Digest, out var expectedSha256))
        {
            throw new InvalidDataException("The Prism Launcher release has no valid SHA-256 digest.");
        }

        using var response = await _httpClient.GetAsync(
            new Uri(asset.BrowserDownloadUrl),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureGitHubDownloadUri(response.RequestMessage?.RequestUri);
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The Prism Launcher portable archive exceeds the safe limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long completed = 0;
        long lastReportedMegabytes = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            completed = checked(completed + read);
            if (completed > MaximumArchiveBytes || completed > asset.Size)
            {
                throw new InvalidDataException("The Prism Launcher portable archive exceeds its published size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var completedMegabytes = completed / 1024 / 1024;
            if (completedMegabytes != lastReportedMegabytes)
            {
                lastReportedMegabytes = completedMegabytes;
                progress?.Report(
                    $"Downloading verified managed launcher… {completedMegabytes:N0} of {asset.Size / 1024d / 1024d:0.0} MB");
            }
        }

        await destination.FlushAsync(cancellationToken);
        if (completed != asset.Size)
        {
            throw new InvalidDataException("The Prism Launcher portable archive size does not match GitHub's release metadata.");
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Prism Launcher portable archive failed SHA-256 verification.");
        }
    }

    private static async Task ExtractArchiveSafelyAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The Prism Launcher portable archive contains an unsafe number of files.");
        }

        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException("The Prism Launcher portable archive contains an unsupported link.");
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("The Prism Launcher portable archive expands beyond the safe limit.");
            }

            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = EnsureContainedPath(destinationDirectory, relativePath);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parent = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidDataException("A launcher archive entry has no parent directory.");
            Directory.CreateDirectory(parent);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private static void CopyClientDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceDirectory, destinationDirectory));
        var entryCount = 0;
        long copiedBytes = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(current.Source).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaximumClientEntries)
                {
                    throw new InvalidDataException("The client output contains too many files for a safe launcher instance.");
                }

                if (entry.LinkTarget is not null
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        $"The client output contains an unsupported linked item: {entry.Name}");
                }

                var destinationPath = Path.Combine(current.Destination, entry.Name);
                if (entry is DirectoryInfo directory)
                {
                    Directory.CreateDirectory(destinationPath);
                    pending.Push((directory.FullName, destinationPath));
                }
                else if (entry is FileInfo file)
                {
                    copiedBytes = checked(copiedBytes + file.Length);
                    if (copiedBytes > MaximumClientBytes)
                    {
                        throw new InvalidDataException("The client output exceeds the 8 GB safe copy limit.");
                    }

                    file.CopyTo(destinationPath, overwrite: false);
                }
            }
        }
    }

    private static async Task WriteInstanceFilesAsync(
        string stagingDirectory,
        ManagedLauncherInstallRequest request,
        string? loaderUid,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var components = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["uid"] = "net.minecraft",
                ["version"] = request.MinecraftVersion.Trim(),
                ["important"] = true
            }
        };
        if (loaderUid is not null)
        {
            components.Add(new Dictionary<string, object?>
            {
                ["uid"] = loaderUid,
                ["version"] = request.LoaderVersion.Trim()
            });
        }

        await WriteJsonAsync(
            Path.Combine(stagingDirectory, "mmc-pack.json"),
            new { formatVersion = 1, components },
            cancellationToken);
        var displayName = request.PackName.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var instanceConfiguration = $"""
            [General]
            InstanceType=OneSix
            JoinServerOnLaunch=false
            OverrideCommands=false
            OverrideConsole=false
            OverrideGameTime=false
            OverrideJavaArgs=false
            OverrideJavaLocation=false
            OverrideMemory=false
            OverrideNativeWorkarounds=false
            OverrideWindow=false
            name={displayName}
            """;
        await File.WriteAllTextAsync(
            Path.Combine(stagingDirectory, "instance.cfg"),
            instanceConfiguration,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(stagingDirectory, "minecraft-server-manager-instance.json"),
            new
            {
                formatVersion = 1,
                instanceId,
                name = request.PackName,
                minecraftVersion = request.MinecraftVersion,
                loaderId = request.LoaderId,
                loaderVersion = request.LoaderVersion,
                recommendedJavaMajor = request.RecommendedJavaMajor,
                sourceClientDirectory = request.ClientDirectory,
                sourceManifest = request.ManifestPath,
                createdAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private static async Task WriteInstallMetadataAsync(
        string stagingDirectory,
        string version,
        GitHubReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(
            Path.Combine(stagingDirectory, InstallMetadataName),
            new
            {
                formatVersion = 1,
                product = "Prism Launcher",
                version,
                asset = asset.Name,
                sha256 = asset.Digest["sha256:".Length..],
                installedAtUtc = DateTimeOffset.UtcNow,
                projectUrl = "https://prismlauncher.org/",
                sourceUrl = $"https://github.com/PrismLauncher/PrismLauncher/tree/{version}",
                license = "GPL-3.0-only",
                licenseUrl = $"https://github.com/PrismLauncher/PrismLauncher/blob/{version}/LICENSE",
                notice = "Downloaded unmodified from Prism Launcher's official GitHub release. Prism Launcher is not affiliated with Mojang or Microsoft."
            },
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(stagingDirectory, "MINECRAFT SERVER MANAGER - THIRD PARTY.txt"),
            $"""
            Prism Launcher {version}

            This is an unmodified portable build downloaded from Prism Launcher's official GitHub release.
            Project: https://prismlauncher.org/
            Source for this version: https://github.com/PrismLauncher/PrismLauncher/tree/{version}
            License: GNU General Public License v3.0 only
            License text: https://github.com/PrismLauncher/PrismLauncher/blob/{version}/LICENSE

            Prism Launcher is not affiliated with Mojang or Microsoft.
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static async Task WriteJsonAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            output,
            value,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    private static void MarkManifestPlayable(
        string manifestPath,
        string instanceId,
        string instanceDirectory,
        string launcherVersion)
    {
        var file = new FileInfo(manifestPath);
        if (file.LinkTarget is not null || file.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The pack audit manifest is linked or exceeds the safe limit.");
        }

        var root = JsonNode.Parse(
            File.ReadAllText(manifestPath),
            new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
            ?? throw new InvalidDataException("The pack audit manifest is not a JSON object.");
        root["clientPlayable"] = true;
        root["clientLauncher"] = "Prism Launcher portable";
        root["clientLauncherVersion"] = launcherVersion;
        root["managedLauncherInstanceId"] = instanceId;
        root["managedLauncherInstanceDirectory"] = instanceDirectory;
        var temporaryPath = manifestPath + $".msm-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private string ReadInstalledVersion()
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(LauncherDirectory, InstallMetadataName)));
            if (document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString()))
            {
                return version.GetString()!;
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return "portable";
    }

    private static string ValidateClientDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The built client folder does not exist: {path}");
        }

        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null
            || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The built client folder is a link and cannot be copied safely.");
        }

        return path;
    }

    private static string ValidateManifest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The pack audit manifest does not exist.", path);
        }

        return path;
    }

    private static void ValidatePackIdentity(ManagedLauncherInstallRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoaderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoaderVersion);
        if (request.PackName.Length > 80
            || request.MinecraftVersion.Length > 64
            || request.LoaderId.Length > 64
            || request.LoaderVersion.Length > 128)
        {
            throw new InvalidDataException("The client pack identity exceeds the safe launcher limits.");
        }
    }

    internal static string? ResolveLoaderUid(string loaderId) => loaderId.Trim().ToLowerInvariant() switch
    {
        "minecraft" => null,
        "forge" => "net.minecraftforge",
        "neoforge" => "net.neoforged",
        "fabric-loader" => "net.fabricmc.fabric-loader",
        "quilt-loader" => "org.quiltmc.quilt-loader",
        _ => throw new NotSupportedException(
            $"{loaderId} cannot yet be represented as a managed launcher client component.")
    };

    private static string CreateUniqueInstanceId(string instancesDirectory, string packName)
    {
        var builder = new StringBuilder("msm-");
        var lastWasSeparator = true;
        foreach (var character in packName.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }

            if (builder.Length >= 52)
            {
                break;
            }
        }

        var baseId = builder.ToString().TrimEnd('-');
        if (baseId.Equals("msm", StringComparison.Ordinal))
        {
            baseId = "msm-pack";
        }

        for (var suffix = 1; suffix <= 999; suffix++)
        {
            var candidate = suffix == 1 ? baseId : $"{baseId}-{suffix}";
            if (!Directory.Exists(Path.Combine(instancesDirectory, candidate))
                && !File.Exists(Path.Combine(instancesDirectory, candidate)))
            {
                return candidate;
            }
        }

        throw new IOException("No safe unique launcher instance name is available for this pack.");
    }

    private static void ValidateExtractedLauncher(string directory)
    {
        if (!File.Exists(Path.Combine(directory, LauncherExecutableName))
            || !File.Exists(Path.Combine(directory, PortableMarkerName)))
        {
            throw new InvalidDataException(
                "The verified Prism Launcher archive does not contain the expected portable Windows files.");
        }
    }

    private static string EnsureContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A launcher archive entry has an invalid path.");
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A launcher archive entry escapes the installation folder.");
        }

        return candidate;
    }

    private static void EnsureOrdinaryDirectory(string path, bool createIfMissing)
    {
        if (!Directory.Exists(path))
        {
            if (!createIfMissing)
            {
                throw new DirectoryNotFoundException(path);
            }

            Directory.CreateDirectory(path);
        }

        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null
            || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"The managed launcher path is a link and cannot be used safely: {path}");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool TryParseSha256Digest(string digest, out string sha256)
    {
        const string prefix = "sha256:";
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        sha256 = value;
        return true;
    }

    private static void EnsureUri(Uri? uri, string expectedHost)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Launcher metadata redirected to an unexpected host.");
        }
    }

    private static void EnsureGitHubDownloadUri(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The launcher download redirected to an unexpected address.");
        }

        var host = uri.Host;
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The launcher download redirected outside GitHub's release service.");
        }
    }

    private static bool OpenLauncherProcess(string executablePath, string? instanceId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("--dir");
        startInfo.ArgumentList.Add(Path.GetDirectoryName(executablePath) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            startInfo.ArgumentList.Add("--show");
            startInfo.ArgumentList.Add(instanceId);
        }

        using var process = Process.Start(startInfo);
        return process is not null;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record LauncherInstallState(string Version, bool InstalledNow);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string Digest { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
