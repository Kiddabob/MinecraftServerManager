using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class TechnicModpackImportService : IModpackImportProvider
{
    private const int MaximumArchiveFiles = 20_000;
    private const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumExtractedFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumExtractedTotalBytes = 20L * 1024 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HashSet<string> ApprovedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "servers.technicpack.net"
    };

    private readonly IProfileService _profileService;
    private readonly IJavaRuntimeService _javaRuntimeService;

    public TechnicModpackImportService(
        IProfileService profileService,
        IJavaRuntimeService javaRuntimeService)
    {
        _profileService = profileService;
        _javaRuntimeService = javaRuntimeService;
    }

    public string ProviderId => "technic";

    public bool CanImport(ModpackCatalogItem pack, ModpackCatalogVersion version) =>
        pack.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
        && version.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
        && pack.ProjectId.Equals(version.ProjectId, StringComparison.Ordinal)
        && version.IsServerCompatible
        && version.PackFile is { PackageKind: ModpackPackageKind.TechnicServerArchive } package
        && IsApprovedSource(package.DownloadUri);

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

        var package = version.PackFile!;
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
        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-server-manager-{operationId}-technic.zip");
        var committed = false;
        try
        {
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.DownloadingPackage,
                $"Downloading {package.FileName} from Technic…"));
            await ModpackImportUtilities.DownloadFileAsync(
                HttpClient,
                package.DownloadUri,
                archivePath,
                expectedSize: -1,
                MaximumArchiveBytes,
                expectedSha512: string.Empty,
                IsApprovedSource,
                (completed, total) => progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.DownloadingPackage,
                    $"Downloading {package.FileName} from Technic…",
                    completed,
                    total)),
                cancellationToken);

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.InspectingPackage,
                "Inspecting the official Technic server archive…"));
            Directory.CreateDirectory(stagingDirectory);
            var installedFileCount = await ExtractArchiveAsync(
                archivePath,
                stagingDirectory,
                progress,
                cancellationToken);

            var minecraftVersion = version.MinecraftVersions.FirstOrDefault() ?? string.Empty;
            var javaExecutable = string.Empty;
            var javaMajor = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion);
            if (javaMajor is not null)
            {
                progress?.Report(new ModpackImportProgress(
                    ModpackImportStage.InstallingServerBaseline,
                    $"Selecting Java {javaMajor} for Minecraft {minecraftVersion}…"));
                javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                    _javaRuntimeService,
                    minecraftVersion,
                    cancellationToken);
            }

            Directory.Move(stagingDirectory, finalDirectory);
            committed = true;

            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.CreatingProfile,
                "Detecting the Technic server launcher…"));
            ProfileImportResult profileImport;
            try
            {
                profileImport = await _profileService.ImportFolderAsync(finalDirectory, cancellationToken);
                if (profileImport.Profile is { } profile)
                {
                    profile.DisplayName = pack.Title;
                    profile.ServerType = version.Loaders.Contains("forge", StringComparer.OrdinalIgnoreCase)
                        ? "Technic Forge"
                        : "Technic";
                    profile.MinecraftVersion = minecraftVersion;
                    if (javaMajor is not null && javaExecutable.Length > 0)
                    {
                        profile.JavaVersion = $"Java {javaMajor}";
                        profile.JavaExecutable = javaExecutable;
                    }

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
                ? $"{pack.Title} {version.VersionNumber} was installed from Technic and added as a server profile. Review and accept the Minecraft EULA before starting it."
                : $"{pack.Title} {version.VersionNumber} was installed, but a runnable launcher was not detected.";
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.Complete,
                message,
                CompletedFiles: installedFileCount,
                TotalFiles: installedFileCount));
            return new ModpackImportResult(
                finalDirectory,
                minecraftVersion,
                version.Loaders.FirstOrDefault() ?? "technic",
                string.Empty,
                installedFileCount,
                profileImport with { Message = message });
        }
        finally
        {
            ModpackImportUtilities.TryDeleteFile(archivePath);
            if (!committed)
            {
                ModpackImportUtilities.TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    internal static async Task<int> ExtractArchiveAsync(
        string archivePath,
        string stagingDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0 || files.Length > MaximumArchiveFiles)
        {
            throw new InvalidDataException("The Technic server archive has an invalid file count.");
        }

        long totalBytes = 0;
        foreach (var entry in files)
        {
            _ = ModpackImportUtilities.NormalizeRelativePath(entry.FullName);
            if (IsSymbolicLink(entry)
                || entry.Length < 0
                || entry.Length > MaximumExtractedFileBytes)
            {
                throw new InvalidDataException($"The archive entry is unsafe: {entry.FullName}");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumExtractedTotalBytes)
            {
                throw new InvalidDataException("The Technic server archive exceeds the 20 GB extraction limit.");
            }
        }

        var commonPrefix = FindCommonRootPrefix(files);
        long completedBytes = 0;
        var installedFiles = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = files[index];
            var normalized = entry.FullName.Replace('\\', '/');
            var relativePath = commonPrefix.Length > 0
                ? normalized[commonPrefix.Length..]
                : normalized;
            if (relativePath.Equals("eula.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = ModpackImportUtilities.ResolveSafePath(
                stagingDirectory,
                relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            progress?.Report(new ModpackImportProgress(
                ModpackImportStage.ExtractingOverrides,
                $"Extracting server file {index + 1} of {files.Length}: {entry.Name}",
                completedBytes,
                totalBytes,
                index,
                files.Length));
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long entryBytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                entryBytes = checked(entryBytes + read);
                if (entryBytes > entry.Length || entryBytes > MaximumExtractedFileBytes)
                {
                    throw new InvalidDataException(
                        $"The archive entry expanded beyond its declared size: {entry.FullName}");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException($"The archive entry is incomplete: {entry.FullName}");
            }

            completedBytes += entryBytes;
            installedFiles++;
        }

        return installedFiles;
    }

    private static string FindCommonRootPrefix(IReadOnlyList<ZipArchiveEntry> entries)
    {
        var paths = entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
        var firstSegments = paths
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        if (firstSegments.Any(segments => segments.Length < 2))
        {
            return string.Empty;
        }

        var root = firstSegments[0][0];
        return firstSegments.All(segments => segments[0].Equals(root, StringComparison.OrdinalIgnoreCase))
            ? $"{root}/"
            : string.Empty;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool IsApprovedSource(Uri uri) =>
        ModpackImportUtilities.IsPublicHttpsUri(uri, ApprovedHosts);

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
