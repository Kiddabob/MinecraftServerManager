using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModrinthModpackImportService : IModpackImportService
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumManifestFiles = 20_000;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumPackFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumPackTotalBytes = 20L * 1024 * 1024 * 1024;
    private const long MaximumOverrideFileBytes = 512L * 1024 * 1024;
    private const long MaximumOverrideTotalBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HashSet<string> ApprovedSourceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };

    private readonly IProfileService _profileService;
    private readonly IReadOnlyList<IServerBaselineInstaller> _baselineInstallers;
    private readonly IJavaRuntimeService _javaRuntimeService;

    public ModrinthModpackImportService(
        IProfileService profileService,
        IEnumerable<IServerBaselineInstaller> baselineInstallers,
        IJavaRuntimeService javaRuntimeService)
    {
        _profileService = profileService;
        _baselineInstallers = baselineInstallers.ToArray();
        _javaRuntimeService = javaRuntimeService;
    }

    public async Task<ModpackImportResult> ImportAsync(
        ModpackCatalogItem pack,
        ModpackCatalogVersion version,
        string destinationParentDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParentDirectory);

        if (!pack.ProviderId.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
            || !version.ProviderId.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
            || !pack.ProjectId.Equals(version.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected project and version are not from the same Modrinth pack.");
        }

        if (!version.IsServerCompatible)
        {
            throw new InvalidOperationException("The publisher has not marked this version as server-compatible.");
        }

        var package = version.PackFile
            ?? throw new InvalidOperationException("The selected version does not provide a .mrpack package.");
        if (package.Size is <= 0 or > MaximumPackageBytes)
        {
            throw new InvalidDataException("The selected .mrpack package has an invalid published size.");
        }

        if (!IsApprovedPackageUri(package.DownloadUri))
        {
            throw new InvalidDataException("The selected .mrpack package is not hosted on Modrinth's secure CDN.");
        }

        var parentDirectory = Path.GetFullPath(destinationParentDirectory);
        if (!Directory.Exists(parentDirectory))
        {
            throw new DirectoryNotFoundException($"The selected install location does not exist: {parentDirectory}");
        }

        var folderName = CreateServerFolderName(pack.Title, version.VersionNumber);
        var finalDirectory = ResolveSafePath(parentDirectory, folderName);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            throw new IOException($"A server folder named '{folderName}' already exists in this location.");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(parentDirectory, $".{folderName}.importing-{operationId}");
        var packagePath = Path.Combine(Path.GetTempPath(), $"minecraft-server-manager-{operationId}.mrpack");
        var committed = false;
        try
        {
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.DownloadingPackage,
                $"Downloading {package.FileName}…",
                TotalBytes: package.Size > 0 ? package.Size : null));
            await DownloadToFileAsync(
                package.DownloadUri,
                packagePath,
                package.Sha512,
                package.Size,
                packageOnly: true,
                (completed, total) => progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.DownloadingPackage,
                    $"Downloading {package.FileName}…",
                    completed,
                    total)),
                cancellationToken);

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.VerifyingPackage,
                "The .mrpack SHA-512 checksum is verified."));

            ModrinthPackManifest manifest;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.InspectingPackage,
                    "Inspecting the server-pack manifest…"));
                manifest = await ReadManifestAsync(archive, cancellationToken);
                ValidateManifestAgainstVersion(manifest, version);

                Directory.CreateDirectory(stagingDirectory);
                await DownloadManifestFilesAsync(
                    manifest,
                    stagingDirectory,
                    progress,
                    cancellationToken);
                await ExtractLayerAsync(
                    archive,
                    "overrides/",
                    stagingDirectory,
                    progress,
                    cancellationToken);
                await ExtractLayerAsync(
                    archive,
                    "server-overrides/",
                    stagingDirectory,
                    progress,
                    cancellationToken);
            }

            var loader = GetLoaderDependency(manifest.Dependencies);
            var baselineInstaller = _baselineInstallers.FirstOrDefault(installer =>
                installer.CanInstall(loader.Key));
            ServerBaselineInstallResult? baselineResult = null;
            if (baselineInstaller is not null)
            {
                progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.InstallingServerBaseline,
                    $"Preparing the {GetLoaderDisplayName(loader.Key)} server baseline…"));
                var baselineProgress = new Progress<string>(message =>
                    progress?.Report(new ModpackImportProgress(
                        ModpackImportStage.InstallingServerBaseline,
                        message)));
                baselineResult = await baselineInstaller.InstallAsync(
                    new ServerBaselineInstallRequest(
                        manifest.Dependencies["minecraft"],
                        loader.Key,
                        loader.Value,
                        stagingDirectory),
                    baselineProgress,
                    cancellationToken);
            }

            var minecraftVersion = manifest.Dependencies["minecraft"];
            var javaMajor = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion)
                ?? throw new InvalidOperationException(
                    $"The Java version required by Minecraft {minecraftVersion} could not be determined.");
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.InstallingServerBaseline,
                $"Selecting Java {javaMajor} for Minecraft {minecraftVersion}…"));
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                minecraftVersion,
                cancellationToken);

            Directory.Move(stagingDirectory, finalDirectory);
            committed = true;

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.CreatingProfile,
                "Checking the prepared folder for a runnable server launcher…"));
            ProfileImportResult profileImport;
            try
            {
                profileImport = await _profileService.ImportFolderAsync(finalDirectory, cancellationToken);
                if (profileImport.Profile is { } profile)
                {
                    ApplyManifestMetadata(
                        profile,
                        pack,
                        version,
                        manifest,
                        loader,
                        javaMajor,
                        javaExecutable);
                    await _profileService.SaveAsync(profile, cancellationToken);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException
                    or InvalidDataException or ArgumentException)
            {
                profileImport = new ProfileImportResult(
                    null,
                    false,
                    $"The files were installed, but profile detection failed: {exception.Message}");
            }

            var installedFileCount = manifest.Files.Count(file => file.ServerSide != "unsupported");
            var message = profileImport.Profile is not null
                ? $"{pack.Title} {version.VersionNumber} was verified, installed, and added as a server profile."
                : baselineResult is null
                    ? $"{pack.Title} {version.VersionNumber} was verified and installed. "
                        + $"The {GetLoaderDisplayName(loader.Key)} server baseline still needs to be installed before it can run."
                    : $"{pack.Title} {version.VersionNumber} and its server baseline were installed, "
                        + "but a runnable launcher was not detected.";
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.Complete,
                message,
                CompletedFiles: installedFileCount,
                TotalFiles: installedFileCount));

            return new ModpackImportResult(
                finalDirectory,
                manifest.Dependencies["minecraft"],
                loader.Key,
                loader.Value,
                installedFileCount,
                profileImport with { Message = message });
        }
        finally
        {
            TryDeleteFile(packagePath);
            if (!committed)
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    internal static ModrinthPackManifest ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("formatVersion", out var formatVersionElement)
            || !formatVersionElement.TryGetInt32(out var formatVersion)
            || formatVersion != 1)
        {
            throw new InvalidDataException("The package does not use supported .mrpack format version 1.");
        }

        if (!GetRequiredString(root, "game").Equals("minecraft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The package is not a Minecraft modpack.");
        }

        var name = GetRequiredString(root, "name");
        var versionId = GetRequiredString(root, "versionId");
        if (!root.TryGetProperty("dependencies", out var dependenciesElement)
            || dependenciesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The package does not declare its Minecraft dependency.");
        }

        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependenciesElement.EnumerateObject())
        {
            if (dependency.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(dependency.Value.GetString()))
            {
                dependencies[dependency.Name] = dependency.Value.GetString()!;
            }
        }

        if (!dependencies.TryGetValue("minecraft", out var minecraftVersion)
            || string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new InvalidDataException("The package does not declare a Minecraft version.");
        }

        string[] loaderKeys = ["neoforge", "forge", "fabric-loader", "quilt-loader"];
        if (loaderKeys.Count(dependencies.ContainsKey) > 1)
        {
            throw new InvalidDataException("The package declares more than one Minecraft mod loader.");
        }

        if (!root.TryGetProperty("files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array
            || filesElement.GetArrayLength() > MaximumManifestFiles)
        {
            throw new InvalidDataException("The package file list is missing or too large.");
        }

        var files = new List<ModrinthPackManifestFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var relativePath = GetRequiredString(fileElement, "path");
            if (!paths.Add(NormalizeRelativePath(relativePath)))
            {
                throw new InvalidDataException($"The package declares the same path more than once: {relativePath}");
            }

            if (!fileElement.TryGetProperty("hashes", out var hashes)
                || hashes.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The package file '{relativePath}' has no checksums.");
            }

            var sha1 = GetRequiredString(hashes, "sha1");
            var sha512 = GetRequiredString(hashes, "sha512");
            if (!IsHexHash(sha1, 40) || !IsHexHash(sha512, 128))
            {
                throw new InvalidDataException($"The package file '{relativePath}' has invalid checksums.");
            }

            if (!fileElement.TryGetProperty("fileSize", out var sizeElement)
                || !sizeElement.TryGetInt64(out var fileSize)
                || fileSize < 0
                || fileSize > MaximumPackFileBytes)
            {
                throw new InvalidDataException($"The package file '{relativePath}' has an invalid size.");
            }

            totalSize = checked(totalSize + fileSize);
            if (totalSize > MaximumPackTotalBytes)
            {
                throw new InvalidDataException("The package exceeds the 20 GB safe import limit.");
            }

            if (!fileElement.TryGetProperty("downloads", out var downloadsElement)
                || downloadsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"The package file '{relativePath}' has no download source.");
            }

            var downloads = downloadsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && IsApprovedSourceUri(uri))
                .Select(value => new Uri(value!))
                .Distinct()
                .ToArray();
            if (downloads.Length == 0)
            {
                throw new InvalidDataException($"The package file '{relativePath}' has no approved HTTPS download source.");
            }

            var serverSide = "required";
            if (fileElement.TryGetProperty("env", out var environment)
                && environment.ValueKind == JsonValueKind.Object
                && environment.TryGetProperty("server", out var serverElement)
                && serverElement.ValueKind == JsonValueKind.String)
            {
                serverSide = serverElement.GetString() ?? "required";
            }

            if (serverSide is not "required" and not "optional" and not "unsupported")
            {
                throw new InvalidDataException($"The package file '{relativePath}' has an invalid server environment.");
            }

            files.Add(new ModrinthPackManifestFile(
                relativePath,
                sha1,
                sha512,
                fileSize,
                serverSide,
                downloads));
        }

        return new ModrinthPackManifest(name, versionId, dependencies, files);
    }

    internal static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootedPrefix = Path.TrimEndingDirectorySeparator(normalizedRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The package path leaves the server folder: {relativePath}");
        }

        return candidate;
    }

    internal static string CreateServerFolderName(string packName, string versionNumber)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var combined = $"{packName} {versionNumber}".Trim();
        var safe = new string(combined
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = "Minecraft Modpack Server";
        }

        return safe.Length <= 100 ? safe : safe[..100].TrimEnd(' ', '.');
    }

    private static async Task<ModrinthPackManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => entry.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1 || entries[0].Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The .mrpack must contain one small modrinth.index.json at its root.");
        }

        await using var stream = entries[0].Open();
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 64 },
            cancellationToken);
        return ParseManifest(document.RootElement);
    }

    private static void ValidateManifestAgainstVersion(
        ModrinthPackManifest manifest,
        ModpackCatalogVersion version)
    {
        var minecraftVersion = manifest.Dependencies["minecraft"];
        if (version.MinecraftVersions.Count > 0
            && !version.MinecraftVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The package declares Minecraft {minecraftVersion}, which does not match the selected version metadata.");
        }
    }

    internal static void ApplyManifestMetadata(
        ServerProfile profile,
        ModpackCatalogItem pack,
        ModpackCatalogVersion version,
        ModrinthPackManifest manifest,
        KeyValuePair<string, string> loader,
        int javaMajor,
        string javaExecutable)
    {
        var minecraftVersion = manifest.Dependencies["minecraft"];
        profile.DisplayName = $"{pack.Title} {version.VersionNumber}";
        profile.ServerType = GetLoaderDisplayName(loader.Key);
        profile.MinecraftVersion = minecraftVersion;
        profile.ForgeVersion = loader.Key is "forge" or "neoforge" ? loader.Value : string.Empty;
        profile.JavaVersion = $"Java {javaMajor}";
        profile.JavaExecutable = javaExecutable;
    }

    private static async Task DownloadManifestFilesAsync(
        ModrinthPackManifest manifest,
        string stagingDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = manifest.Files
            .Where(file => file.ServerSide != "unsupported")
            .ToArray();
        var totalBytes = files.Sum(file => file.FileSize);
        long completedBytes = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var destinationPath = ResolveSafePath(stagingDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.DownloadingFiles,
                $"Downloading server file {index + 1} of {files.Length}: {Path.GetFileName(file.RelativePath)}",
                completedBytes,
                totalBytes,
                index,
                files.Length));

            Exception? lastException = null;
            foreach (var source in file.Downloads)
            {
                try
                {
                    await DownloadToFileAsync(
                        source,
                        destinationPath,
                        file.Sha512,
                        file.FileSize,
                        packageOnly: false,
                        (downloaded, _) => progress?.Report(new ModpackImportProgress(
                            ModpackImportStage.DownloadingFiles,
                            $"Downloading server file {index + 1} of {files.Length}: {Path.GetFileName(file.RelativePath)}",
                            completedBytes + downloaded,
                            totalBytes,
                            index,
                            files.Length)),
                        cancellationToken);
                    lastException = null;
                    break;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or InvalidDataException)
                {
                    lastException = exception;
                    TryDeleteFile(destinationPath);
                }
            }

            if (lastException is not null)
            {
                throw new InvalidDataException(
                    $"The server file '{file.RelativePath}' could not be downloaded and verified.",
                    lastException);
            }

            completedBytes += file.FileSize;
        }
    }

    private static async Task DownloadToFileAsync(
        Uri source,
        string destinationPath,
        string expectedSha512,
        long expectedSize,
        bool packageOnly,
        Action<long, long?>? reportBytes,
        CancellationToken cancellationToken)
    {
        if (packageOnly ? !IsApprovedPackageUri(source) : !IsApprovedSourceUri(source))
        {
            throw new InvalidDataException($"The download source is not approved: {source.Host}");
        }

        using var response = await HttpClient.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("The download did not return a final HTTPS address.");
        if (finalUri.Scheme != Uri.UriSchemeHttps || finalUri.IsLoopback)
        {
            throw new InvalidDataException("The download redirected to an unsafe address.");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (expectedSize > 0 && contentLength is > 0 && contentLength != expectedSize)
        {
            throw new InvalidDataException("The download size does not match its published metadata.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        long completed = 0;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            completed = checked(completed + read);
            if (expectedSize > 0 && completed > expectedSize)
            {
                throw new InvalidDataException("The download is larger than its published metadata.");
            }

            hash.AppendData(buffer, 0, read);
            await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportBytes?.Invoke(completed, expectedSize > 0 ? expectedSize : contentLength);
        }

        await destinationStream.FlushAsync(cancellationToken);
        if (expectedSize > 0 && completed != expectedSize)
        {
            throw new InvalidDataException("The download is incomplete.");
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(expectedSha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded file failed SHA-512 verification.");
        }
    }

    internal static async Task ExtractLayerAsync(
        ZipArchive archive,
        string layerPrefix,
        string stagingDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => NormalizeArchivePath(entry.FullName)
                .StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (entries.Length > MaximumManifestFiles)
        {
            throw new InvalidDataException("The package contains too many override files.");
        }

        long totalBytes = 0;
        foreach (var entry in entries)
        {
            if (IsSymbolicLink(entry) || entry.Length < 0 || entry.Length > MaximumOverrideFileBytes)
            {
                throw new InvalidDataException($"The override entry is unsafe: {entry.FullName}");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumOverrideTotalBytes)
            {
                throw new InvalidDataException("The package overrides exceed the 2 GB safe extraction limit.");
            }
        }

        long completedBytes = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var normalized = NormalizeArchivePath(entry.FullName);
            var relativePath = normalized[layerPrefix.Length..];
            var destinationPath = ResolveSafePath(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.ExtractingOverrides,
                $"Applying {layerPrefix.TrimEnd('/')} file {index + 1} of {entries.Length}…",
                completedBytes,
                totalBytes,
                index,
                entries.Length));

            await using var sourceStream = entry.Open();
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long entryBytes = 0;
            int read;
            while ((read = await sourceStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                entryBytes = checked(entryBytes + read);
                if (entryBytes > entry.Length || entryBytes > MaximumOverrideFileBytes)
                {
                    throw new InvalidDataException($"The override entry expanded beyond its declared size: {entry.FullName}");
                }

                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException($"The override entry is incomplete: {entry.FullName}");
            }

            completedBytes += entryBytes;
        }
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!relativePath.Equals(relativePath.Trim(), StringComparison.Ordinal)
            || normalized.StartsWith('/')
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':')
            || segments.Length == 0
            || segments.Any(segment => segment is "." or ".."
                || !segment.Equals(segment.TrimEnd(' ', '.'), StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The package contains an unsafe path: {relativePath}");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool IsApprovedPackageUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovedSourceUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && ApprovedSourceHosts.Contains(uri.Host);

    private static bool IsHexHash(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidDataException($"The package is missing '{propertyName}'.");
    }

    private static KeyValuePair<string, string> GetLoaderDependency(
        IReadOnlyDictionary<string, string> dependencies)
    {
        string[] loaderKeys = ["neoforge", "forge", "fabric-loader", "quilt-loader"];
        foreach (var key in loaderKeys)
        {
            if (dependencies.TryGetValue(key, out var value))
            {
                return new KeyValuePair<string, string>(key, value);
            }
        }

        var futureLoader = dependencies.FirstOrDefault(dependency =>
            !dependency.Key.Equals("minecraft", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(futureLoader.Key))
        {
            return futureLoader;
        }

        return new KeyValuePair<string, string>("minecraft", dependencies["minecraft"]);
    }

    private static string GetLoaderDisplayName(string loaderId) => loaderId switch
    {
        "fabric-loader" => "Fabric",
        "quilt-loader" => "Quilt",
        "neoforge" => "NeoForge",
        "forge" => "Forge",
        "minecraft" => "Vanilla Minecraft",
        _ => loaderId
    };

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
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }
}

internal sealed record ModrinthPackManifest(
    string Name,
    string VersionId,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<ModrinthPackManifestFile> Files);

internal sealed record ModrinthPackManifestFile(
    string RelativePath,
    string Sha1,
    string Sha512,
    long FileSize,
    string ServerSide,
    IReadOnlyList<Uri> Downloads);
