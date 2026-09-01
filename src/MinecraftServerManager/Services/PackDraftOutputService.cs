using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class PackDraftOutputService : IPackDraftOutputService
{
    private const int MaximumItems = 500;
    private const long MaximumTotalBytes = 4L * 1024L * 1024L * 1024L;
    private const long MaximumEulaFileBytes = 64 * 1024;
    private const string ManifestFileName = "minecraft-server-manager-pack.json";
    private const string ReadmeFileName = "README - Minecraft Server Manager.txt";
    private readonly IPackContentCatalogService _catalogService;
    private readonly IReadOnlyDictionary<string, IPackContentDownloadProvider> _downloadProviders;
    private readonly IReadOnlyList<IServerBaselineInstaller> _baselineInstallers;
    private readonly IJavaRuntimeService _javaRuntimeService;

    public PackDraftOutputService(
        IPackContentCatalogService catalogService,
        IEnumerable<IPackContentDownloadProvider> downloadProviders,
        IEnumerable<IServerBaselineInstaller> baselineInstallers,
        IJavaRuntimeService javaRuntimeService)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _downloadProviders = downloadProviders.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
        _baselineInstallers = baselineInstallers?.ToArray()
            ?? throw new ArgumentNullException(nameof(baselineInstallers));
        _javaRuntimeService = javaRuntimeService
            ?? throw new ArgumentNullException(nameof(javaRuntimeService));
    }

    public async Task<PackOutputPlan> CreatePlanAsync(
        PackOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canBuildEmptyServerBaseline = request.Target == PackBuildTarget.Server
            && !string.IsNullOrWhiteSpace(request.ServerLoaderId)
            && !string.IsNullOrWhiteSpace(request.ServerLoaderVersion);
        if (request.Items.Count > MaximumItems
            || (request.Items.Count == 0 && !canBuildEmptyServerBaseline))
        {
            throw new InvalidDataException(
                $"A content output must contain between 1 and {MaximumItems:N0} items. An empty draft is allowed only for a supported server baseline.");
        }

        var parentDirectory = ValidateParentDirectory(request.DestinationParentDirectory);
        var packName = NormalizePackName(request.PackName);
        ValidateLoaderSelection(request.Target, request.ClientLoaderId, request.ClientLoaderVersion, "client");
        ValidateLoaderSelection(request.Target, request.ServerLoaderId, request.ServerLoaderVersion, "server");
        ValidateLinkedLoaderPair(
            request.Target,
            request.ClientLoaderId,
            request.ClientLoaderVersion,
            request.ServerLoaderId,
            request.ServerLoaderVersion);
        var destinationDirectory = EnsureContainedPath(parentDirectory, Path.Combine(parentDirectory, packName));
        EnsureDestinationAvailable(destinationDirectory);

        var plannedItems = new List<PackOutputPlanItem>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draftItem in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (draftItem.Placement == PackContentPlacement.Review)
            {
                throw new InvalidDataException($"{draftItem.DisplayName} still needs placement review and cannot be downloaded.");
            }

            var identity = $"{draftItem.ProviderId}:{draftItem.VersionId}";
            if (!identities.Add(identity))
            {
                throw new InvalidDataException($"The draft contains {draftItem.DisplayName} more than once.");
            }

            var version = await _catalogService.GetVersionAsync(
                draftItem.ProviderId,
                draftItem.VersionId,
                cancellationToken);
            ValidateVersionIdentity(draftItem, version);
            var file = version.PrimaryFile
                ?? throw new InvalidDataException($"{draftItem.DisplayName} does not publish a downloadable primary JAR.");
            var downloadProvider = GetDownloadProvider(draftItem.ProviderId);
            downloadProvider.ValidateFile(file);
            var relativePaths = GetRelativePaths(request.Target, draftItem, file.FileName);
            foreach (var relativePath in relativePaths)
            {
                if (!destinations.Add(relativePath))
                {
                    throw new InvalidDataException(
                        $"More than one draft item would create {relativePath.Replace(Path.DirectorySeparatorChar, '/')}.");
                }
            }

            plannedItems.Add(new PackOutputPlanItem(draftItem, file, relativePaths));
        }

        var totalBytes = plannedItems.Sum(item => item.File.Size);
        if (totalBytes < 0
            || (totalBytes == 0 && !canBuildEmptyServerBaseline)
            || totalBytes > MaximumTotalBytes)
        {
            throw new InvalidDataException("The pack download is empty or exceeds the 4 GB safety limit.");
        }

        var minecraftVersion = request.MinecraftVersion.Trim();
        var recommendedJavaMajor = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion);
        if (!string.IsNullOrWhiteSpace(request.ServerLoaderId) && recommendedJavaMajor is null)
        {
            throw new InvalidDataException(
                $"The Java baseline for Minecraft {minecraftVersion} could not be determined safely.");
        }

        return new PackOutputPlan(
            packName,
            request.Target,
            minecraftVersion,
            request.ClientPlatformId.Trim(),
            request.ServerPlatformId.Trim(),
            request.ClientLoaderId.Trim(),
            request.ClientLoaderVersion.Trim(),
            request.ServerLoaderId.Trim(),
            request.ServerLoaderVersion.Trim(),
            recommendedJavaMajor,
            destinationDirectory,
            plannedItems);
    }

    public async Task<PackOutputResult> CreateOutputAsync(
        PackOutputPlan plan,
        IProgress<PackOutputProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count > MaximumItems
            || (plan.Items.Count == 0 && !plan.PreparesServerBaseline)
            || plan.TotalBytes < 0
            || plan.TotalBytes > MaximumTotalBytes)
        {
            throw new InvalidDataException("The pack output plan does not contain a safe number or size of files.");
        }

        var destinationDirectory = Path.GetFullPath(plan.DestinationDirectory);
        var parentDirectory = ValidateParentDirectory(Path.GetDirectoryName(destinationDirectory)
            ?? throw new InvalidDataException("The pack output destination has no parent directory."));
        destinationDirectory = EnsureContainedPath(parentDirectory, destinationDirectory);
        EnsureDestinationAvailable(destinationDirectory);

        var stagingDirectory = EnsureContainedPath(
            parentDirectory,
            Path.Combine(parentDirectory, $".msm-pack-{Guid.NewGuid():N}"));
        var downloadDirectory = Path.Combine(stagingDirectory, ".downloads");
        Directory.CreateDirectory(downloadDirectory);
        long completedBytes = 0;
        var arrangedFiles = 0;
        ServerBaselineInstallResult? baselineResult = null;
        try
        {
            progress?.Report(new PackOutputProgress(
                PackOutputStage.Resolving,
                "Preparing the verified download staging area…",
                0,
                plan.TotalBytes,
                0,
                plan.Items.Count));

            for (var index = 0; index < plan.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = plan.Items[index];
                var provider = GetDownloadProvider(item.ProviderId);
                provider.ValidateFile(item.File);
                var downloadedPath = Path.Combine(downloadDirectory, $"{index:D4}-{item.File.FileName}");
                var completedBefore = completedBytes;
                progress?.Report(new PackOutputProgress(
                    PackOutputStage.Downloading,
                    $"Downloading {item.DisplayName} from {item.ProviderId}…",
                    completedBytes,
                    plan.TotalBytes,
                    index,
                    plan.Items.Count));
                await provider.DownloadAndVerifyAsync(
                    item.File,
                    downloadedPath,
                    downloaded => progress?.Report(new PackOutputProgress(
                        PackOutputStage.Downloading,
                        $"Downloading {item.File.FileName}…",
                        completedBefore + downloaded,
                        plan.TotalBytes,
                        index,
                        plan.Items.Count)),
                    cancellationToken);
                completedBytes += item.File.Size;

                progress?.Report(new PackOutputProgress(
                    PackOutputStage.Arranging,
                    $"Placing {item.DisplayName} on the correct side…",
                    completedBytes,
                    plan.TotalBytes,
                    index + 1,
                    plan.Items.Count));
                foreach (var relativePath in item.RelativePaths)
                {
                    var destinationPath = EnsureContainedPath(stagingDirectory, Path.Combine(stagingDirectory, relativePath));
                    var destinationParent = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidDataException("A pack content destination has no parent directory.");
                    Directory.CreateDirectory(destinationParent);
                    File.Copy(downloadedPath, destinationPath, overwrite: false);
                    arrangedFiles++;
                }
            }

            Directory.Delete(downloadDirectory, recursive: true);
            if (plan.PreparesServerBaseline)
            {
                var serverDirectory = Path.Combine(stagingDirectory, "Server");
                Directory.CreateDirectory(serverDirectory);
                var installers = _baselineInstallers
                    .Where(installer => installer.CanInstall(plan.ServerLoaderId))
                    .ToArray();
                if (installers.Length != 1)
                {
                    throw new InvalidOperationException(
                        installers.Length == 0
                            ? $"No safe server baseline installer is registered for {plan.ServerLoaderId}."
                            : $"More than one server baseline installer is registered for {plan.ServerLoaderId}.");
                }

                progress?.Report(new PackOutputProgress(
                    PackOutputStage.InstallingServerBaseline,
                    $"Preparing {plan.ServerLoaderId} {plan.ServerLoaderVersion} for Minecraft {plan.MinecraftVersion}…"));
                baselineResult = await installers[0].InstallAsync(
                    new ServerBaselineInstallRequest(
                        plan.MinecraftVersion,
                        plan.ServerLoaderId,
                        plan.ServerLoaderVersion,
                        serverDirectory),
                    new Progress<string>(message => progress?.Report(new PackOutputProgress(
                        PackOutputStage.InstallingServerBaseline,
                        message))),
                    cancellationToken);
                if (ServerFolderDetector.Detect(serverDirectory) is null)
                {
                    throw new InvalidDataException(
                        "The loader installer completed without creating a detectable server launcher.");
                }

                EnsureEulaPending(serverDirectory);
            }

            progress?.Report(new PackOutputProgress(
                PackOutputStage.WritingManifest,
                "Writing the auditable pack manifest…",
                plan.TotalBytes,
                plan.TotalBytes,
                plan.Items.Count,
                plan.Items.Count));
            var manifestPath = Path.Combine(stagingDirectory, ManifestFileName);
            await WriteManifestAsync(manifestPath, plan, baselineResult, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, ReadmeFileName),
                CreateReadme(plan, baselineResult),
                cancellationToken);

            EnsureDestinationAvailable(destinationDirectory);
            Directory.Move(stagingDirectory, destinationDirectory);
            var finalManifestPath = Path.Combine(destinationDirectory, ManifestFileName);
            progress?.Report(new PackOutputProgress(
                PackOutputStage.Complete,
                baselineResult is null
                    ? $"Created verified content bundle: {destinationDirectory}"
                    : $"Created the verified pack and runnable server baseline: {destinationDirectory}",
                plan.TotalBytes,
                plan.TotalBytes,
                plan.Items.Count,
                plan.Items.Count));
            return new PackOutputResult(
                destinationDirectory,
                plan.Items.Count,
                arrangedFiles,
                finalManifestPath,
                baselineResult is not null,
                baselineResult is null ? string.Empty : Path.Combine(destinationDirectory, "Server"),
                baselineResult?.LauncherFileName ?? string.Empty,
                plan.RecommendedJavaMajor);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Keep the original operation result. The uniquely named staging folder
                    // contains no unverified files outside this one output attempt.
                }
            }
        }
    }

    internal static string NormalizePackName(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || name is "." or ".." || name.Length > 80)
        {
            throw new ArgumentException("Enter a pack name between 1 and 80 characters.", nameof(value));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith(' ')
            || name.EndsWith('.'))
        {
            throw new ArgumentException("The pack name contains characters Windows cannot use in a folder name.", nameof(value));
        }

        return name;
    }

    private IPackContentDownloadProvider GetDownloadProvider(string providerId) =>
        _downloadProviders.TryGetValue(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"{providerId} does not have a safe verified download adapter yet.");

    private static void ValidateVersionIdentity(PackDraftItem draftItem, ServerContentVersion version)
    {
        if (!version.ProviderId.Equals(draftItem.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !version.ProjectId.Equals(draftItem.ProjectId, StringComparison.OrdinalIgnoreCase)
            || !version.VersionId.Equals(draftItem.VersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{draftItem.DisplayName} returned mismatched provider version metadata.");
        }
    }

    private static void ValidateLoaderSelection(
        PackBuildTarget target,
        string loaderId,
        string loaderVersion,
        string side)
    {
        var hasId = !string.IsNullOrWhiteSpace(loaderId);
        var hasVersion = !string.IsNullOrWhiteSpace(loaderVersion);
        if (hasId != hasVersion)
        {
            throw new InvalidDataException(
                $"The {side} loader family and exact version must be selected together.");
        }

        if (side == "client" && target == PackBuildTarget.Server && (hasId || hasVersion))
        {
            throw new InvalidDataException("A server-only output cannot contain a client loader selection.");
        }

        if (side == "server" && target == PackBuildTarget.Client && (hasId || hasVersion))
        {
            throw new InvalidDataException("A client-only output cannot contain a server loader selection.");
        }
    }

    private static void ValidateLinkedLoaderPair(
        PackBuildTarget target,
        string clientLoaderId,
        string clientLoaderVersion,
        string serverLoaderId,
        string serverLoaderVersion)
    {
        if (target != PackBuildTarget.ClientAndServer
            || string.IsNullOrWhiteSpace(clientLoaderId)
            || string.IsNullOrWhiteSpace(serverLoaderId))
        {
            return;
        }

        if (!clientLoaderId.Trim().Equals(serverLoaderId.Trim(), StringComparison.OrdinalIgnoreCase)
            || !clientLoaderVersion.Trim().Equals(
                serverLoaderVersion.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A linked client and server must use the same loader family and exact loader version.");
        }
    }

    private static IReadOnlyList<string> GetRelativePaths(
        PackBuildTarget target,
        PackDraftItem item,
        string fileName)
    {
        var paths = new List<string>(2);
        if (item.Placement is PackContentPlacement.Client or PackContentPlacement.Both)
        {
            if (target == PackBuildTarget.Server || item.Kind == ServerContentKind.Plugin)
            {
                throw new InvalidDataException($"{item.DisplayName} cannot be placed in the client output.");
            }

            paths.Add(Path.Combine("Client", "mods", fileName));
        }

        if (item.Placement is PackContentPlacement.Server or PackContentPlacement.Both)
        {
            if (target == PackBuildTarget.Client)
            {
                throw new InvalidDataException($"{item.DisplayName} cannot be placed in the server output.");
            }

            paths.Add(Path.Combine(
                "Server",
                item.Kind == ServerContentKind.Plugin ? "plugins" : "mods",
                fileName));
        }

        if (paths.Count == 0)
        {
            throw new InvalidDataException($"{item.DisplayName} has no safe output placement.");
        }

        return paths;
    }

    private static string ValidateParentDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parentDirectory = Path.GetFullPath(value);
        if (!Directory.Exists(parentDirectory))
        {
            throw new DirectoryNotFoundException($"The selected output folder does not exist: {parentDirectory}");
        }

        if (new DirectoryInfo(parentDirectory).LinkTarget is not null)
        {
            throw new InvalidDataException("The selected output folder is a link and cannot be used safely.");
        }

        return parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            throw new InvalidDataException("The pack output path escapes its selected parent folder.");
        }

        return normalizedCandidate;
    }

    private static void EnsureDestinationAvailable(string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new IOException(
                $"{Path.GetFileName(destinationDirectory)} already exists. Pack outputs are never overwritten automatically.");
        }
    }

    private static async Task WriteManifestAsync(
        string manifestPath,
        PackOutputPlan plan,
        ServerBaselineInstallResult? baselineResult,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            formatVersion = 1,
            name = plan.PackName,
            target = plan.Target.ToString(),
            minecraftVersion = plan.MinecraftVersion,
            clientPlatform = plan.ClientPlatformId,
            clientLoaderId = plan.ClientLoaderId,
            clientLoaderVersion = plan.ClientLoaderVersion,
            serverPlatform = plan.ServerPlatformId,
            serverLoaderId = plan.ServerLoaderId,
            serverLoaderVersion = plan.ServerLoaderVersion,
            recommendedJavaMajor = plan.RecommendedJavaMajor,
            generatedAtUtc = DateTimeOffset.UtcNow,
            contentOnly = baselineResult is null,
            clientPrepared = plan.Target is PackBuildTarget.Client or PackBuildTarget.ClientAndServer,
            clientPlayable = false,
            minecraftLauncherProfileId = string.Empty,
            minecraftLauncherVersionId = string.Empty,
            serverBaselinePrepared = baselineResult is not null,
            serverLauncher = baselineResult?.LauncherFileName ?? string.Empty,
            serverEulaAccepted = false,
            items = plan.Items.Select(item => new
            {
                provider = item.ProviderId,
                projectId = item.DraftItem.ProjectId,
                versionId = item.DraftItem.VersionId,
                name = item.DisplayName,
                version = item.DraftItem.VersionNumber,
                kind = item.DraftItem.Kind.ToString(),
                placement = item.DraftItem.Placement.ToString(),
                dependency = item.DraftItem.IsDependency,
                file = item.File.FileName,
                size = item.File.Size,
                sha512 = item.File.Sha512,
                sha1 = item.File.Sha1,
                paths = item.RelativePaths.Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
            })
        };
        await using var output = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            output,
            manifest,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    private static string CreateReadme(
        PackOutputPlan plan,
        ServerBaselineInstallResult? baselineResult)
    {
        var serverState = baselineResult is null
            ? "No runnable server baseline was prepared for this platform."
            : $"A runnable {plan.ServerLoaderId} {plan.ServerLoaderVersion} server baseline was prepared in Server\\ using {baselineResult.LauncherFileName}.";
        var clientState = plan.Target is PackBuildTarget.Client or PackBuildTarget.ClientAndServer
            ? "The Client folder is an isolated game directory. Minecraft Server Manager can copy it into its manager-owned portable launcher as a separate instance; the normal Minecraft Launcher remains untouched and account authentication stays inside the managed launcher."
            : "No client output was requested.";
        return $"""
        Minecraft Server Manager content bundle
        =======================================

        Pack: {plan.PackName}
        Minecraft: {plan.MinecraftVersion}
        Target: {plan.Target}
        Client platform: {plan.ClientPlatformId}
        Client loader: {plan.ClientLoaderId} {plan.ClientLoaderVersion}
        Server platform: {plan.ServerPlatformId}
        Server loader: {plan.ServerLoaderId} {plan.ServerLoaderVersion}
        Recommended Java: {(plan.RecommendedJavaMajor is null ? "Not determined" : $"Java {plan.RecommendedJavaMajor}")}

        This output contains verified mod/plugin files and an auditable manifest.
        {serverState}
        {clientState}

        No Minecraft EULA has been accepted by creating this bundle.
        A prepared server must be imported into the manager and explicitly enabled
        through its EULA review before it can start.
        Existing output folders are never overwritten automatically.
        """;
    }

    private static void EnsureEulaPending(string serverDirectory)
    {
        var eulaPath = Path.Combine(serverDirectory, "eula.txt");
        if (!File.Exists(eulaPath))
        {
            return;
        }

        var fileInfo = new FileInfo(eulaPath);
        if (fileInfo.LinkTarget is not null || fileInfo.Length > MaximumEulaFileBytes)
        {
            throw new InvalidDataException(
                "The staged server contains an unsafe eula.txt. No EULA state was changed.");
        }

        foreach (var line in File.ReadLines(eulaPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0
                || !trimmed[..separator].Trim().Equals("eula", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (bool.TryParse(trimmed[(separator + 1)..].Trim(), out var accepted) && accepted)
            {
                throw new InvalidDataException(
                    "The staged server unexpectedly contained eula=true. The output was rolled back so acceptance can remain an explicit in-app choice.");
            }
        }
    }
}
