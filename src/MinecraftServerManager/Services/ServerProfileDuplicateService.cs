using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerProfileDuplicateService : IServerProfileDuplicateService
{
    private static readonly HashSet<string> TransientTopLevelDirectories = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "logs",
        "crash-reports",
        "backups",
        "cache",
        ".cache"
    };

    private readonly IProfileService _profileService;

    public ServerProfileDuplicateService(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task<ServerProfileDuplicateResult> DuplicateAsync(
        ServerProfileDuplicateRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationParentDirectory);

        var sourceDirectory = Path.GetFullPath(request.SourceProfile.ServerDirectory);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The source server folder no longer exists: {sourceDirectory}");
        }

        var destinationParent = Path.GetFullPath(request.DestinationParentDirectory);
        if (!Directory.Exists(destinationParent))
        {
            throw new DirectoryNotFoundException(
                $"The selected destination does not exist: {destinationParent}");
        }

        if (IsSameOrChildPath(destinationParent, sourceDirectory))
        {
            throw new InvalidOperationException(
                "Choose a destination outside the server being duplicated.");
        }

        var displayName = request.DisplayName.Trim();
        var folderName = ModpackImportUtilities.CreateServerFolderName(displayName, string.Empty);
        var finalDirectory = ResolveAvailableDestination(destinationParent, folderName, sourceDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(
            destinationParent,
            $".{Path.GetFileName(finalDirectory)}.copying-{operationId}");
        var committed = false;
        try
        {
            progress?.Report("Preparing a separate editable copy…");
            Directory.CreateDirectory(stagingDirectory);
            var excludedWorldRoots = request.IncludeWorldData
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : ReadWorldRoots(sourceDirectory);
            var copiedFileCount = await CopyDirectoryAsync(
                new DirectoryInfo(sourceDirectory),
                sourceDirectory,
                stagingDirectory,
                excludedWorldRoots,
                progress,
                cancellationToken);
            Directory.Move(stagingDirectory, finalDirectory);
            committed = true;

            progress?.Report("Detecting the copied server and creating its profile…");
            ProfileImportResult profileImport;
            try
            {
                profileImport = await _profileService.ImportFolderAsync(
                    finalDirectory,
                    cancellationToken);
                if (profileImport.Profile is { } profile)
                {
                    ApplySourceProfileSettings(profile, request.SourceProfile, displayName);
                    await _profileService.SaveAsync(profile, cancellationToken);
                    profileImport = profileImport with
                    {
                        Message = $"Created the editable server copy '{displayName}'."
                    };
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException
                    or InvalidDataException or ArgumentException)
            {
                profileImport = new ProfileImportResult(
                    null,
                    false,
                    $"The server files were copied, but profile detection failed: {exception.Message}");
            }

            return new ServerProfileDuplicateResult(
                finalDirectory,
                copiedFileCount,
                request.IncludeWorldData,
                profileImport);
        }
        finally
        {
            if (!committed)
            {
                ModpackImportUtilities.TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    internal static HashSet<string> ReadWorldRoots(string sourceDirectory)
    {
        var levelName = "world";
        var propertiesPath = Path.Combine(sourceDirectory, "server.properties");
        if (File.Exists(propertiesPath))
        {
            foreach (var line in File.ReadLines(propertiesPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0
                    || !trimmed[..separator].Trim().Equals(
                        "level-name",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed[(separator + 1)..].Trim();
                if (value.Length > 0)
                {
                    levelName = value;
                }

                break;
            }
        }

        try
        {
            var normalized = ModpackImportUtilities.NormalizeRelativePath(levelName);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                normalized,
                $"{normalized}_nether",
                $"{normalized}_the_end"
            };
        }
        catch (InvalidDataException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "world",
                "world_nether",
                "world_the_end"
            };
        }
    }

    internal static bool ShouldExcludeRelativePath(
        string relativePath,
        bool isDirectory,
        IReadOnlySet<string> excludedWorldRoots)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var firstSegment = normalized.Split('/', 2)[0];
        if (isDirectory && TransientTopLevelDirectories.Contains(firstSegment))
        {
            return true;
        }

        if (excludedWorldRoots.Any(worldRoot =>
                normalized.Equals(worldRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith($"{worldRoot}/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalized);
        return !isDirectory
            && (normalized.Equals("eula.txt", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("session.lock", StringComparison.OrdinalIgnoreCase)
                || firstSegment.StartsWith(".msm-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> CopyDirectoryAsync(
        DirectoryInfo directory,
        string sourceRoot,
        string stagingRoot,
        IReadOnlySet<string> excludedWorldRoots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotLink(directory);
        var copiedFiles = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotLink(entry);
            var relativePath = Path.GetRelativePath(sourceRoot, entry.FullName)
                .Replace('\\', '/');
            var isDirectory = entry is DirectoryInfo;
            if (ShouldExcludeRelativePath(relativePath, isDirectory, excludedWorldRoots))
            {
                continue;
            }

            var destinationPath = ModpackImportUtilities.ResolveSafePath(stagingRoot, relativePath);
            if (entry is DirectoryInfo childDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                copiedFiles += await CopyDirectoryAsync(
                    childDirectory,
                    sourceRoot,
                    stagingRoot,
                    excludedWorldRoots,
                    progress,
                    cancellationToken);
                continue;
            }

            progress?.Report($"Copying {relativePath}…");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = new FileStream(
                entry.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            copiedFiles++;
        }

        return copiedFiles;
    }

    private static string ResolveAvailableDestination(
        string destinationParent,
        string requestedFolderName,
        string sourceDirectory)
    {
        for (var copyNumber = 1; copyNumber <= 1_000; copyNumber++)
        {
            var folderName = copyNumber == 1
                ? requestedFolderName
                : $"{requestedFolderName} {copyNumber}";
            var candidate = ModpackImportUtilities.ResolveSafePath(destinationParent, folderName);
            if (!Directory.Exists(candidate)
                && !File.Exists(candidate)
                && !PathsEqual(candidate, sourceDirectory))
            {
                return candidate;
            }
        }

        throw new IOException("A unique destination folder could not be found for the copy.");
    }

    private static void ApplySourceProfileSettings(
        ServerProfile target,
        ServerProfile source,
        string displayName)
    {
        target.DisplayName = displayName;
        target.ServerType = source.ServerType;
        target.MinecraftVersion = source.MinecraftVersion;
        target.ForgeVersion = source.ForgeVersion;
        target.JavaVersion = source.JavaVersion;
        target.JavaExecutable = source.JavaExecutable;
        target.ServerJar = source.ServerJar;
        target.LaunchScript = source.LaunchScript;
        target.JavaArguments = source.JavaArguments.ToArray();
        target.ServerArguments = source.ServerArguments.ToArray();
        target.DirectLaunchArguments = source.DirectLaunchArguments.ToArray();
        target.RequiredFiles = source.RequiredFiles.ToArray();
        target.RequiredDirectories = source.RequiredDirectories.ToArray();
        target.ConfigurationSources = source.ConfigurationSources.ToArray();
        target.ConfigurationSchemas = source.ConfigurationSchemas.ToArray();
        target.MapPointsOfInterest = source.MapPointsOfInterest
            .Select(point => new WorldMapPointOfInterest
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = point.Name,
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                DimensionId = point.DimensionId,
                DimensionKey = point.DimensionKey
            })
            .ToArray();
        target.ReadyPatterns = source.ReadyPatterns.ToArray();
        target.FailurePatterns = source.FailurePatterns.ToArray();
        target.PlayerJoinPatterns = source.PlayerJoinPatterns.ToArray();
        target.PlayerLeavePatterns = source.PlayerLeavePatterns.ToArray();
        target.ListPlayersCommand = source.ListPlayersCommand;
        target.BroadcastCommandPrefix = source.BroadcastCommandPrefix;
        target.SaveCommand = source.SaveCommand;
        target.StopCommand = source.StopCommand;
        target.StopTimeoutSeconds = source.StopTimeoutSeconds;
    }

    private static void EnsureNotLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0
            && entry.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"The server contains a linked file or folder that cannot be copied safely: {entry.FullName}");
        }
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
