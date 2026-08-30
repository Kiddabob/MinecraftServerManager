using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class FtbModpackImportService : IModpackImportProvider
{
    private const int MaximumManifestFiles = 20_000;
    private const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 20L * 1024 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HashSet<string> ApprovedFileHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "files.feed-the-beast.com",
        "cdn.feed-the-beast.com",
        "edge.forgecdn.net",
        "mediafilez.forgecdn.net"
    };

    private readonly IProfileService _profileService;
    private readonly IReadOnlyList<IServerBaselineInstaller> _baselineInstallers;
    private readonly IJavaRuntimeService _javaRuntimeService;

    public FtbModpackImportService(
        IProfileService profileService,
        IEnumerable<IServerBaselineInstaller> baselineInstallers,
        IJavaRuntimeService javaRuntimeService)
    {
        _profileService = profileService;
        _baselineInstallers = baselineInstallers.ToArray();
        _javaRuntimeService = javaRuntimeService;
    }

    public string ProviderId => "ftb";

    public bool CanImport(ModpackCatalogItem pack, ModpackCatalogVersion version) =>
        pack.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
        && version.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
        && pack.ProjectId.Equals(version.ProjectId, StringComparison.Ordinal)
        && version.IsServerCompatible
        && version.PackFile is { PackageKind: ModpackPackageKind.FtbManifest } manifest
        && IsApprovedManifestUri(manifest.DownloadUri);

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
        if (!CanImport(pack, version))
        {
            throw new InvalidOperationException(version.ImportReadinessText);
        }

        var parentDirectory = Path.GetFullPath(destinationParentDirectory);
        if (!Directory.Exists(parentDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The selected install location does not exist: {parentDirectory}");
        }

        var folderName = ModpackImportUtilities.CreateServerFolderName(
            pack.Title,
            version.VersionNumber);
        var finalDirectory = ModpackImportUtilities.ResolveSafePath(parentDirectory, folderName);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            throw new IOException(
                $"A server folder named '{folderName}' already exists in this location.");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(parentDirectory, $".{folderName}.importing-{operationId}");
        var committed = false;
        try
        {
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.InspectingPackage,
                "Loading the first-party FTB server manifest…"));
            using var response = await HttpClient.GetAsync(
                version.PackFile!.DownloadUri,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalManifestUri = response.RequestMessage?.RequestUri
                ?? throw new InvalidDataException("FTB did not return a manifest address.");
            if (!IsApprovedManifestUri(finalManifestUri))
            {
                throw new InvalidDataException("The FTB manifest redirected to an unapproved host.");
            }

            await using var manifestStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                manifestStream,
                new JsonDocumentOptions { MaxDepth = 64 },
                cancellationToken);
            var manifest = ParseManifest(document.RootElement, pack, version);
            Directory.CreateDirectory(stagingDirectory);
            await DownloadFilesAsync(manifest, stagingDirectory, progress, cancellationToken);

            var baselineInstaller = _baselineInstallers.FirstOrDefault(installer =>
                installer.CanInstall(manifest.LoaderId));
            if (baselineInstaller is null)
            {
                throw new NotSupportedException(
                    $"The FTB pack uses the unsupported loader '{manifest.LoaderId}'.");
            }

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.InstallingServerBaseline,
                $"Preparing the {ModpackImportUtilities.GetLoaderDisplayName(manifest.LoaderId)} server baseline…"));
            var baselineProgress = new Progress<string>(message =>
                progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.InstallingServerBaseline,
                    message)));
            await baselineInstaller.InstallAsync(
                new ServerBaselineInstallRequest(
                    manifest.MinecraftVersion,
                    manifest.LoaderId,
                    manifest.LoaderVersion,
                    stagingDirectory),
                baselineProgress,
                cancellationToken);

            var javaMajor = _javaRuntimeService.GetRecommendedJavaMajor(manifest.MinecraftVersion)
                ?? throw new InvalidOperationException(
                    $"The Java version required by Minecraft {manifest.MinecraftVersion} could not be determined.");
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.InstallingServerBaseline,
                $"Selecting Java {javaMajor} for Minecraft {manifest.MinecraftVersion}…"));
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                manifest.MinecraftVersion,
                cancellationToken);

            Directory.Move(stagingDirectory, finalDirectory);
            committed = true;

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.CreatingProfile,
                "Detecting the prepared FTB server launcher…"));
            ProfileImportResult profileImport;
            try
            {
                profileImport = await _profileService.ImportFolderAsync(finalDirectory, cancellationToken);
                if (profileImport.Profile is { } profile)
                {
                    profile.DisplayName = $"{pack.Title} {version.VersionNumber}";
                    profile.ServerType = $"FTB {ModpackImportUtilities.GetLoaderDisplayName(manifest.LoaderId)}";
                    profile.MinecraftVersion = manifest.MinecraftVersion;
                    profile.ForgeVersion = manifest.LoaderId is "forge" or "neoforge"
                        ? manifest.LoaderVersion
                        : string.Empty;
                    profile.JavaVersion = $"Java {javaMajor}";
                    profile.JavaExecutable = javaExecutable;
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

            var message = profileImport.Profile is not null
                ? $"{pack.Title} {version.VersionNumber} was verified file by file, installed, and added as a server profile. Review and accept the Minecraft EULA before starting it."
                : $"{pack.Title} {version.VersionNumber} was verified and installed, but a runnable launcher was not detected.";
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.Complete,
                message,
                CompletedFiles: manifest.Files.Count,
                TotalFiles: manifest.Files.Count));
            return new ModpackImportResult(
                finalDirectory,
                manifest.MinecraftVersion,
                manifest.LoaderId,
                manifest.LoaderVersion,
                manifest.Files.Count,
                profileImport with { Message = message });
        }
        finally
        {
            if (!committed)
            {
                ModpackImportUtilities.TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    internal static FtbServerManifest ParseManifest(
        JsonElement root,
        ModpackCatalogItem pack,
        ModpackCatalogVersion version)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !GetString(root, "status").Equals("success", StringComparison.OrdinalIgnoreCase)
            || GetInt32(root, "parent").ToString() != pack.ProjectId
            || GetInt32(root, "id").ToString() != version.VersionId)
        {
            throw new InvalidDataException("FTB returned a manifest for a different pack or version.");
        }

        if (!root.TryGetProperty("targets", out var targets)
            || targets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The FTB manifest has no platform targets.");
        }

        var targetList = targets.EnumerateArray()
            .Select(target => new FtbManifestTarget(
                GetString(target, "name").ToLowerInvariant(),
                GetString(target, "version"),
                GetString(target, "type").ToLowerInvariant()))
            .ToArray();
        var minecraft = targetList.SingleOrDefault(target =>
            target.Type == "game" && target.Name == "minecraft")
            ?? throw new InvalidDataException("The FTB manifest has no unique Minecraft target.");
        var loader = targetList.SingleOrDefault(target => target.Type == "modloader")
            ?? throw new InvalidDataException("The FTB manifest has no unique mod-loader target.");
        if (minecraft.Version.Length == 0 || loader.Name.Length == 0 || loader.Version.Length == 0)
        {
            throw new InvalidDataException("The FTB manifest has incomplete platform targets.");
        }

        if (!root.TryGetProperty("files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The FTB manifest has no files.");
        }

        var files = new List<FtbManifestFile>();
        long totalBytes = 0;
        foreach (var file in filesElement.EnumerateArray())
        {
            if (files.Count >= MaximumManifestFiles)
            {
                throw new InvalidDataException("The FTB manifest contains too many files.");
            }

            var name = GetString(file, "name");
            var path = GetString(file, "path");
            if (name.Length == 0 || path.Length == 0)
            {
                throw new InvalidDataException("The FTB manifest contains a file with no path.");
            }

            var relativePath = ModpackImportUtilities.NormalizeRelativePath($"{path}/{name}");
            if (relativePath.Equals("eula.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = GetInt64(file, "size");
            if (size < 0 || size > MaximumFileBytes)
            {
                throw new InvalidDataException($"The FTB file '{relativePath}' has an invalid size.");
            }

            totalBytes = checked(totalBytes + size);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException("The FTB server pack exceeds the 20 GB safe import limit.");
            }

            if (!file.TryGetProperty("hashes", out var hashes)
                || hashes.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The FTB file '{relativePath}' has no hashes.");
            }

            var sha512 = GetString(hashes, "sha512");
            if (!ModpackImportUtilities.IsHexHash(sha512, 128))
            {
                throw new InvalidDataException($"The FTB file '{relativePath}' has no valid SHA-512 hash.");
            }

            var sources = new List<Uri>();
            AddApprovedSource(sources, GetString(file, "url"));
            if (file.TryGetProperty("mirrors", out var mirrors)
                && mirrors.ValueKind == JsonValueKind.Array)
            {
                foreach (var mirror in mirrors.EnumerateArray()
                             .Where(item => item.ValueKind == JsonValueKind.String))
                {
                    AddApprovedSource(sources, mirror.GetString() ?? string.Empty);
                }
            }

            if (sources.Count == 0)
            {
                throw new InvalidDataException(
                    $"The FTB file '{relativePath}' has no approved HTTPS source.");
            }

            files.Add(new FtbManifestFile(
                relativePath,
                size,
                sha512,
                sources.Distinct().ToArray()));
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException("The FTB server manifest contains no installable files.");
        }

        return new FtbServerManifest(
            minecraft.Version,
            NormalizeLoaderId(loader.Name),
            loader.Version,
            files);
    }

    private static async Task DownloadFilesAsync(
        FtbServerManifest manifest,
        string stagingDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completedFiles = 0;
        long completedBytes = 0;
        var totalBytes = manifest.Files.Sum(file => file.Size);
        await Parallel.ForEachAsync(
            manifest.Files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 6
            },
            async (file, token) =>
            {
                var destinationPath = ModpackImportUtilities.ResolveSafePath(
                    stagingDirectory,
                    file.RelativePath);
                Exception? lastException = null;
                foreach (var source in file.Sources)
                {
                    ModpackImportUtilities.TryDeleteFile(destinationPath);
                    try
                    {
                        await ModpackImportUtilities.DownloadFileAsync(
                            HttpClient,
                            source,
                            destinationPath,
                            file.Size,
                            MaximumFileBytes,
                            file.Sha512,
                            IsApprovedFileSource,
                            reportBytes: null,
                            token);
                        lastException = null;
                        break;
                    }
                    catch (Exception exception) when (
                        exception is HttpRequestException or IOException or InvalidDataException)
                    {
                        lastException = exception;
                    }
                }

                if (lastException is not null)
                {
                    ModpackImportUtilities.TryDeleteFile(destinationPath);
                    throw new InvalidDataException(
                        $"The FTB file '{file.RelativePath}' could not be downloaded and verified.",
                        lastException);
                }

                var filesDone = Interlocked.Increment(ref completedFiles);
                var bytesDone = Interlocked.Add(ref completedBytes, file.Size);
                progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.DownloadingFiles,
                    $"Verified FTB server file {filesDone:N0} of {manifest.Files.Count:N0}: {Path.GetFileName(file.RelativePath)}",
                    bytesDone,
                    totalBytes,
                    filesDone,
                    manifest.Files.Count));
            });
    }

    private static string NormalizeLoaderId(string loaderId) => loaderId switch
    {
        "fabric-loader" => "fabric",
        "quilt-loader" => "quilt",
        _ => loaderId
    };

    private static void AddApprovedSource(ICollection<Uri> sources, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsApprovedFileSource(uri))
        {
            sources.Add(uri);
        }
    }

    private static bool IsApprovedManifestUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("api.feed-the-beast.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(
            "/v1/modpacks/public/modpack/",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovedFileSource(Uri uri) =>
        ModpackImportUtilities.IsPublicHttpsUri(uri, ApprovedFileHosts);

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : -1;

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

internal sealed record FtbServerManifest(
    string MinecraftVersion,
    string LoaderId,
    string LoaderVersion,
    IReadOnlyList<FtbManifestFile> Files);

internal sealed record FtbManifestTarget(string Name, string Version, string Type);

internal sealed record FtbManifestFile(
    string RelativePath,
    long Size,
    string Sha512,
    IReadOnlyList<Uri> Sources);
