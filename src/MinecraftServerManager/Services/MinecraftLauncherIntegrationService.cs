using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class MinecraftLauncherIntegrationService : IMinecraftLauncherIntegrationService
{
    private const long MaximumProfileBytes = 32L * 1024L * 1024L;
    private const long MaximumLoaderProfileBytes = 4L * 1024L * 1024L;
    private const string MinecraftLauncherAppId =
        "Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft";
    private static readonly Uri ForgeMetadataUri = new(
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
    private static readonly Uri NeoForgeMetadataUri = new(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly HttpClient _httpClient;
    private readonly Func<bool> _isLauncherRunning;
    private readonly Func<bool> _openLauncher;

    public MinecraftLauncherIntegrationService(IJavaRuntimeService javaRuntimeService)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft"),
            CreateHttpClient(),
            javaRuntimeService,
            IsLauncherProcessRunning,
            OpenMinecraftLauncher)
    {
    }

    internal MinecraftLauncherIntegrationService(
        string launcherDirectory,
        HttpClient httpClient,
        IJavaRuntimeService javaRuntimeService,
        Func<bool>? isLauncherRunning = null,
        Func<bool>? openLauncher = null)
    {
        LauncherDirectory = Path.GetFullPath(launcherDirectory);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _javaRuntimeService = javaRuntimeService
            ?? throw new ArgumentNullException(nameof(javaRuntimeService));
        _isLauncherRunning = isLauncherRunning ?? (() => false);
        _openLauncher = openLauncher ?? (() => true);
    }

    public string LauncherDirectory { get; }

    public async Task<MinecraftLauncherInstallResult> InstallAsync(
        MinecraftLauncherInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (_isLauncherRunning())
        {
            throw new InvalidOperationException(
                "Close Minecraft and Minecraft Launcher before adding this pack. The launcher writes its profiles when it closes and could otherwise discard the new installation.");
        }

        var launcherDirectory = ValidateLauncherDirectory();
        var profilesPath = FindLauncherProfilesPath(launcherDirectory);
        var backupPath = CreateProfileBackup(profilesPath, launcherDirectory);
        var profileBackups = new List<(string ProfilesPath, string BackupPath)>
        {
            (profilesPath, backupPath)
        };
        try
        {
            progress?.Report("Preparing the exact client loader for Minecraft Launcher…");
            var versionId = await InstallLoaderAsync(
                request,
                launcherDirectory,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Forge-family installers may update one of the two supported profile files.
            // Select the current file again before adding the dedicated pack entry.
            var currentProfilesPath = FindLauncherProfilesPath(launcherDirectory);
            if (!currentProfilesPath.Equals(profilesPath, StringComparison.OrdinalIgnoreCase))
            {
                profileBackups.Add((
                    currentProfilesPath,
                    CreateProfileBackup(currentProfilesPath, launcherDirectory)));
                profilesPath = currentProfilesPath;
            }
            progress?.Report("Adding the dedicated client game directory to Minecraft Launcher…");
            var profileId = RegisterProfile(
                profilesPath,
                request.PackName,
                request.ClientDirectory,
                versionId);
            MarkManifestPlayable(
                request.ManifestPath,
                profileId,
                versionId,
                profilesPath);
            return new MinecraftLauncherInstallResult(
                profileId,
                versionId,
                profilesPath,
                backupPath,
                request.ClientDirectory,
                $"{request.PackName} is ready in Minecraft Launcher. Its isolated game directory is {request.ClientDirectory}.");
        }
        catch
        {
            foreach (var profileBackup in profileBackups)
            {
                RestoreProfileBackup(profileBackup.BackupPath, profileBackup.ProfilesPath);
            }

            throw;
        }
    }

    public bool TryOpenLauncher(out string message)
    {
        try
        {
            if (_openLauncher())
            {
                message = "Minecraft Launcher is opening. The new pack installation is selected and ready for Play.";
                return true;
            }

            message = "Minecraft Launcher could not be opened. Open it from the Start menu and choose the new installation.";
            return false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            message = $"Minecraft Launcher could not be opened: {exception.Message}";
            return false;
        }
    }

    internal static string RegisterProfile(
        string profilesPath,
        string packName,
        string clientDirectory,
        string versionId)
    {
        var fileInfo = new FileInfo(profilesPath);
        if (!fileInfo.Exists || fileInfo.LinkTarget is not null || fileInfo.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException("Minecraft Launcher profiles are missing, linked, or too large to update safely.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(
                    File.ReadAllText(profilesPath),
                    new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                    new JsonDocumentOptions { MaxDepth = 128 })
                as JsonObject
                ?? throw new InvalidDataException("Minecraft Launcher profiles do not contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Minecraft Launcher profiles contain invalid JSON.", exception);
        }

        JsonObject profiles;
        if (root["profiles"] is null)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }
        else if (root["profiles"] is JsonObject existingProfiles)
        {
            profiles = existingProfiles;
        }
        else
        {
            throw new InvalidDataException("Minecraft Launcher profiles contain an invalid profiles collection.");
        }

        var profileId = CreateProfileId(clientDirectory);
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        profiles[profileId] = new JsonObject
        {
            ["created"] = now,
            ["gameDir"] = Path.GetFullPath(clientDirectory),
            ["icon"] = "Furnace",
            ["lastUsed"] = now,
            ["lastVersionId"] = versionId,
            ["name"] = packName,
            ["type"] = "custom"
        };
        root["selectedProfile"] = profileId;
        WriteJsonAtomically(profilesPath, root);
        return profileId;
    }

    internal static string ValidateLoaderProfile(
        JsonElement root,
        string minecraftVersion,
        string loaderName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !IsSafeVersionId(idElement.GetString(), out var versionId)
            || !root.TryGetProperty("inheritsFrom", out var inheritsElement)
            || inheritsElement.ValueKind != JsonValueKind.String
            || !minecraftVersion.Equals(inheritsElement.GetString(), StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("mainClass", out var mainClass)
            || mainClass.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(mainClass.GetString())
            || !root.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array
            || libraries.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                $"{loaderName} returned an invalid Minecraft Launcher profile for {minecraftVersion}.");
        }

        return versionId;
    }

    private async Task<string> InstallLoaderAsync(
        MinecraftLauncherInstallRequest request,
        string launcherDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken) => request.LoaderId.Trim().ToLowerInvariant() switch
        {
            "minecraft" => request.MinecraftVersion,
            "fabric-loader" => await InstallPublishedProfileAsync(
                new Uri(
                    "https://meta.fabricmc.net/v2/versions/loader/"
                    + $"{Uri.EscapeDataString(request.MinecraftVersion)}/"
                    + $"{Uri.EscapeDataString(request.LoaderVersion)}/profile/json"),
                "meta.fabricmc.net",
                "Fabric",
                request.MinecraftVersion,
                launcherDirectory,
                cancellationToken),
            "quilt-loader" => await InstallPublishedProfileAsync(
                new Uri(
                    "https://meta.quiltmc.org/v3/versions/loader/"
                    + $"{Uri.EscapeDataString(request.MinecraftVersion)}/"
                    + $"{Uri.EscapeDataString(request.LoaderVersion)}/profile/json"),
                "meta.quiltmc.org",
                "Quilt",
                request.MinecraftVersion,
                launcherDirectory,
                cancellationToken),
            "forge" => await InstallForgeFamilyAsync(
                request,
                launcherDirectory,
                isNeoForge: false,
                progress,
                cancellationToken),
            "neoforge" => await InstallForgeFamilyAsync(
                request,
                launcherDirectory,
                isNeoForge: true,
                progress,
                cancellationToken),
            _ => throw new NotSupportedException(
                $"The official Minecraft Launcher integration does not support '{request.LoaderId}' yet.")
        };

    private async Task<string> InstallPublishedProfileAsync(
        Uri profileUri,
        string expectedHost,
        string loaderName,
        string minecraftVersion,
        string launcherDirectory,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            profileUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        ValidateOfficialUri(response.RequestMessage?.RequestUri, expectedHost);
        if (response.Content.Headers.ContentLength is > MaximumLoaderProfileBytes)
        {
            throw new InvalidDataException($"{loaderName} returned an oversized launcher profile.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > MaximumLoaderProfileBytes)
            {
                throw new InvalidDataException($"{loaderName} returned an oversized launcher profile.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var profileBytes = output.ToArray();
        using var document = JsonDocument.Parse(
            profileBytes,
            new JsonDocumentOptions { MaxDepth = 128 });
        var versionId = ValidateLoaderProfile(document.RootElement, minecraftVersion, loaderName);
        var versionsDirectory = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(launcherDirectory, "versions"));
        Directory.CreateDirectory(versionsDirectory);
        var versionDirectory = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(versionsDirectory, versionId));
        if (Directory.Exists(versionDirectory)
            && new DirectoryInfo(versionDirectory).LinkTarget is not null)
        {
            throw new InvalidDataException("The target Minecraft version folder is a link and cannot be updated safely.");
        }

        Directory.CreateDirectory(versionDirectory);
        var profilePath = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(versionDirectory, versionId + ".json"));
        if (File.Exists(profilePath))
        {
            var existingInfo = new FileInfo(profilePath);
            if (existingInfo.LinkTarget is not null || existingInfo.Length > MaximumLoaderProfileBytes)
            {
                throw new InvalidDataException("The existing Minecraft loader profile is unsafe to reuse.");
            }

            using var existing = JsonDocument.Parse(
                File.ReadAllBytes(profilePath),
                new JsonDocumentOptions { MaxDepth = 128 });
            ValidateLoaderProfile(existing.RootElement, minecraftVersion, loaderName);
            return versionId;
        }

        WriteBytesAtomically(profilePath, profileBytes);
        return versionId;
    }

    private async Task<string> InstallForgeFamilyAsync(
        MinecraftLauncherInstallRequest request,
        string launcherDirectory,
        bool isNeoForge,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var loaderName = isNeoForge ? "NeoForge" : "Forge";
        var mavenHost = isNeoForge ? "maven.neoforged.net" : "maven.minecraftforge.net";
        progress?.Report($"Resolving the exact {loaderName} client installer…");
        var metadata = await JavaServerInstallerUtilities.DownloadMavenMetadataAsync(
            isNeoForge ? NeoForgeMetadataUri : ForgeMetadataUri,
            mavenHost,
            cancellationToken);
        var artifactVersion = isNeoForge
            ? JavaServerInstallerUtilities.ResolveExactArtifactVersion(
                JavaServerInstallerUtilities.ParseMavenVersions(metadata),
                loaderName,
                request.LoaderVersion)
            : JavaServerInstallerUtilities.ResolveForgeArtifactVersion(
                JavaServerInstallerUtilities.ParseMavenVersions(metadata),
                request.MinecraftVersion,
                request.LoaderVersion);
        var installerFileName = isNeoForge
            ? $"neoforge-{artifactVersion}-installer.jar"
            : $"forge-{artifactVersion}-installer.jar";
        var installerUri = isNeoForge
            ? new Uri(
                $"https://{mavenHost}/releases/net/neoforged/neoforge/{Uri.EscapeDataString(artifactVersion)}/{Uri.EscapeDataString(installerFileName)}")
            : new Uri(
                $"https://{mavenHost}/net/minecraftforge/forge/{Uri.EscapeDataString(artifactVersion)}/{Uri.EscapeDataString(installerFileName)}");
        var installerPath = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(launcherDirectory, $".msm-client-installer-{Guid.NewGuid():N}.jar"));
        try
        {
            await JavaServerInstallerUtilities.DownloadVerifiedInstallerAsync(
                installerUri,
                installerPath,
                mavenHost,
                progress,
                cancellationToken);
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                request.MinecraftVersion,
                cancellationToken);
            progress?.Report(
                $"Installing {loaderName} {request.LoaderVersion} into Minecraft Launcher…");
            await JavaServerInstallerUtilities.RunInstallerAsync(
                javaExecutable,
                installerPath,
                launcherDirectory,
                ["--installClient", launcherDirectory],
                progress,
                cancellationToken);
            return FindInstalledVersionId(
                launcherDirectory,
                request.MinecraftVersion,
                request.LoaderVersion,
                isNeoForge ? "neoforge" : "forge");
        }
        finally
        {
            JavaServerInstallerUtilities.TryDeleteFile(installerPath);
        }
    }

    private static string FindInstalledVersionId(
        string launcherDirectory,
        string minecraftVersion,
        string loaderVersion,
        string loaderId)
    {
        var versionsDirectory = Path.Combine(launcherDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            throw new InvalidDataException("The loader installer did not create a Minecraft versions folder.");
        }

        var candidates = new List<(string Id, DateTime LastWriteUtc)>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory).Take(5000))
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.LinkTarget is not null)
            {
                continue;
            }

            foreach (var jsonPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(4))
            {
                try
                {
                    var info = new FileInfo(jsonPath);
                    if (info.LinkTarget is not null || info.Length is <= 0 or > MaximumLoaderProfileBytes)
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(
                        File.ReadAllBytes(jsonPath),
                        new JsonDocumentOptions { MaxDepth = 128 });
                    var root = document.RootElement;
                    var id = root.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString()?.Trim() ?? string.Empty
                            : Path.GetFileNameWithoutExtension(jsonPath);
                    var inheritsFrom = root.TryGetProperty("inheritsFrom", out var inheritsElement)
                        && inheritsElement.ValueKind == JsonValueKind.String
                            ? inheritsElement.GetString()?.Trim() ?? string.Empty
                            : string.Empty;
                    var loaderMatches = id.Contains(loaderId, StringComparison.OrdinalIgnoreCase)
                        && (loaderId != "forge"
                            || !id.Contains("neoforge", StringComparison.OrdinalIgnoreCase));
                    if (loaderMatches
                        && id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)
                        && (inheritsFrom.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase)
                            || id.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add((id, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastWriteUtc)
            .Select(candidate => candidate.Id)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"The loader installer finished without creating a detectable {loaderId} client version.");
    }

    private string ValidateLauncherDirectory()
    {
        if (!Directory.Exists(LauncherDirectory))
        {
            throw new DirectoryNotFoundException(
                "Minecraft Launcher has not created its data folder yet. Open Minecraft Launcher once, close it, and retry.");
        }

        var directoryInfo = new DirectoryInfo(LauncherDirectory);
        if (directoryInfo.LinkTarget is not null)
        {
            throw new InvalidDataException("The Minecraft Launcher data folder is a link and cannot be updated safely.");
        }

        return LauncherDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FindLauncherProfilesPath(string launcherDirectory)
    {
        var candidates = new[]
            {
                Path.Combine(launcherDirectory, "launcher_profiles_microsoft_store.json"),
                Path.Combine(launcherDirectory, "launcher_profiles.json")
            }
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Where(info => info.LinkTarget is null && info.Length <= MaximumProfileBytes)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToArray();
        return candidates.FirstOrDefault()?.FullName
            ?? throw new FileNotFoundException(
                "Minecraft Launcher has not created a launcher profile yet. Open Minecraft: Java Edition once, close the launcher completely, and retry.");
    }

    private static string CreateProfileBackup(string profilesPath, string launcherDirectory)
    {
        var backupDirectory = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(launcherDirectory, "minecraft-server-manager-backups"));
        if (Directory.Exists(backupDirectory)
            && new DirectoryInfo(backupDirectory).LinkTarget is not null)
        {
            throw new InvalidDataException("The Minecraft Launcher backup folder is a link and cannot be used safely.");
        }

        Directory.CreateDirectory(backupDirectory);
        var backupPath = EnsureContainedPath(
            launcherDirectory,
            Path.Combine(
                backupDirectory,
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Path.GetFileName(profilesPath)}"));
        File.Copy(profilesPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void RestoreProfileBackup(string backupPath, string profilesPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, profilesPath, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateRequest(MinecraftLauncherInstallRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoaderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoaderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ManifestPath);
        var clientDirectory = Path.GetFullPath(request.ClientDirectory);
        if (!Directory.Exists(clientDirectory)
            || new DirectoryInfo(clientDirectory).LinkTarget is not null)
        {
            throw new DirectoryNotFoundException(
                "The built client game directory is missing or is a link and cannot be registered safely.");
        }

        var manifestPath = Path.GetFullPath(request.ManifestPath);
        if (!File.Exists(manifestPath)
            || new FileInfo(manifestPath).LinkTarget is not null)
        {
            throw new FileNotFoundException("The built pack manifest is missing or linked.", manifestPath);
        }
    }

    private static void MarkManifestPlayable(
        string manifestPath,
        string profileId,
        string versionId,
        string profilesPath)
    {
        var info = new FileInfo(manifestPath);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException("The built pack manifest is missing, linked, or too large to update safely.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(
                    File.ReadAllText(manifestPath),
                    new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                    new JsonDocumentOptions { MaxDepth = 128 })
                as JsonObject
                ?? throw new InvalidDataException("The built pack manifest is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The built pack manifest contains invalid JSON.", exception);
        }

        root["contentOnly"] = false;
        root["clientPlayable"] = true;
        root["minecraftLauncherProfileId"] = profileId;
        root["minecraftLauncherVersionId"] = versionId;
        root["minecraftLauncherProfilesFile"] = Path.GetFileName(profilesPath);
        WriteJsonAtomically(manifestPath, root);
    }

    private static string CreateProfileId(string clientDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(clientDirectory).ToUpperInvariant()));
        return "minecraft-server-manager-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string EnsureContainedPath(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Minecraft Launcher integration path escapes its data folder.");
        }

        return normalizedCandidate;
    }

    private static void WriteJsonAtomically(string path, JsonObject root)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            root,
            new JsonSerializerOptions { WriteIndented = true });
        WriteBytesAtomically(path, bytes, overwrite: true);
    }

    private static void WriteBytesAtomically(string path, byte[] bytes, bool overwrite = false)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The target file has no parent directory.");
        var temporaryPath = Path.Combine(directory, $".msm-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       32 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            JavaServerInstallerUtilities.TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsSafeVersionId(string? value, out string versionId)
    {
        versionId = value?.Trim() ?? string.Empty;
        return versionId.Length is > 0 and <= 160
            && versionId.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or '+');
    }

    private static void ValidateOfficialUri(Uri? uri, string expectedHost)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The client loader profile came from an unexpected host.");
        }
    }

    private static bool IsLauncherProcessRunning()
    {
        foreach (var processName in new[] { "MinecraftLauncher", "Minecraft", "GameLaunchHelper" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    private static bool OpenMinecraftLauncher()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{MinecraftLauncherAppId}",
            UseShellExecute = true
        });
        return process is not null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Kidda.MinecraftServerManager/0.2 (+https://github.com/Kiddabob/MinecraftServerManager)");
        return client;
    }
}
