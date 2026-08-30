using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerContentInstallService : IServerContentInstallService
{
    private const int MaximumPlanItems = 100;
    private const long MaximumFileLength = 512L * 1024 * 1024;
    private const long MaximumPlanLength = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly IServerContentCatalogService _catalogService;

    public ServerContentInstallService(IServerContentCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public async Task<ServerContentInstallPlan> CreatePlanAsync(
        ServerProfile profile,
        ServerContentTarget target,
        ServerContentProject project,
        ServerContentVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        if (project.Kind != target.Kind)
        {
            throw new InvalidOperationException($"{project.Title} is not published as a {target.KindText.ToLowerInvariant()} item.");
        }

        var serverRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(profile.ServerDirectory));
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException($"The server directory does not exist: {serverRoot}");
        }

        var destination = ResolveContainedDirectory(serverRoot, target.DirectoryName);
        var items = new List<ServerContentInstallPlanItem>();
        var warnings = new List<string>();
        var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await AddVersionAsync(
            profile,
            target,
            version,
            project.Title,
            isDependency: false,
            destination,
            items,
            warnings,
            projects,
            versions,
            cancellationToken);

        var duplicateFile = items
            .GroupBy(item => item.File.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFile is not null)
        {
            throw new InvalidDataException($"The dependency plan contains more than one file named {duplicateFile.Key}.");
        }

        var totalLength = items.Sum(item => item.File.Size);
        if (totalLength <= 0 || totalLength > MaximumPlanLength)
        {
            throw new InvalidDataException("The content download plan is empty or exceeds the 2 GB safety limit.");
        }

        return new ServerContentInstallPlan(
            serverRoot,
            destination,
            target.Kind,
            items,
            warnings);
    }

    public async Task<ServerContentInstallResult> InstallAsync(
        ServerContentInstallPlan plan,
        IProgress<ServerContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count == 0 || plan.Items.Count > MaximumPlanItems)
        {
            throw new InvalidDataException("The content install plan does not contain a safe number of files.");
        }

        if (plan.TotalBytes <= 0 || plan.TotalBytes > MaximumPlanLength)
        {
            throw new InvalidDataException("The content install plan is empty or exceeds the 2 GB safety limit.");
        }

        foreach (var item in plan.Items)
        {
            ValidateFile(item.File);
            if (item.Kind != plan.Kind)
            {
                throw new InvalidDataException("The content install plan mixes mods and plugins.");
            }
        }

        var serverRoot = Path.GetFullPath(plan.ServerDirectory);
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException($"The server directory does not exist: {serverRoot}");
        }

        var destination = EnsureContainedPath(serverRoot, plan.DestinationDirectory);
        EnsureNotLink(serverRoot, "server directory");
        if (Directory.Exists(destination))
        {
            EnsureNotLink(destination, "content directory");
        }
        else
        {
            Directory.CreateDirectory(destination);
        }

        EnsureNoCollisions(destination, plan.Items);

        var stagingRoot = Path.Combine(serverRoot, $".msm-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var stagedFiles = new List<(ServerContentInstallPlanItem Item, string Path)>();
        var installedFiles = new List<string>();
        long completedBytes = 0;

        try
        {
            for (var index = 0; index < plan.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = plan.Items[index];
                ValidateFile(item.File);
                var stagedPath = Path.Combine(stagingRoot, $"{index:D3}-{item.File.FileName}");
                progress?.Report(new ServerContentInstallProgress(
                    ServerContentInstallStage.Downloading,
                    $"Downloading {item.DisplayName}…",
                    completedBytes,
                    plan.TotalBytes));
                await DownloadAndVerifyAsync(
                    item.File,
                    stagedPath,
                    completedBytes,
                    plan.TotalBytes,
                    progress,
                    cancellationToken);
                completedBytes += item.File.Size;
                stagedFiles.Add((item, stagedPath));
            }

            EnsureNoCollisions(destination, plan.Items);
            progress?.Report(new ServerContentInstallProgress(
                ServerContentInstallStage.Installing,
                "Moving verified files into the server…",
                plan.TotalBytes,
                plan.TotalBytes));

            try
            {
                foreach (var staged in stagedFiles)
                {
                    var destinationPath = Path.Combine(destination, staged.Item.File.FileName);
                    File.Move(staged.Path, destinationPath);
                    installedFiles.Add(destinationPath);
                }
            }
            catch (Exception moveException)
            {
                var rollbackFailures = new List<string>();
                foreach (var installedFile in installedFiles)
                {
                    try
                    {
                        if (File.Exists(installedFile))
                        {
                            File.Delete(installedFile);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        rollbackFailures.Add(Path.GetFileName(installedFile));
                    }
                }

                if (rollbackFailures.Count > 0)
                {
                    throw new IOException(
                        $"The install could not be completed, and these newly added files could not be removed: {string.Join(", ", rollbackFailures)}.",
                        moveException);
                }

                throw;
            }

            progress?.Report(new ServerContentInstallProgress(
                ServerContentInstallStage.Complete,
                $"Installed {installedFiles.Count:N0} verified files.",
                plan.TotalBytes,
                plan.TotalBytes));
            return new ServerContentInstallResult(
                installedFiles.Count,
                destination,
                installedFiles.Select(Path.GetFileName).Cast<string>().ToArray());
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A partial staging copy can be removed later. Do not turn an otherwise
                // successful install into a failure, or hide the original download error.
            }
        }
    }

    private async Task AddVersionAsync(
        ServerProfile profile,
        ServerContentTarget target,
        ServerContentVersion version,
        string displayName,
        bool isDependency,
        string destination,
        List<ServerContentInstallPlanItem> items,
        List<string> warnings,
        Dictionary<string, string> projects,
        HashSet<string> versions,
        CancellationToken cancellationToken)
    {
        if (versions.Contains(version.VersionId))
        {
            return;
        }

        if (projects.TryGetValue(version.ProjectId, out var existingVersionId))
        {
            if (existingVersionId.Equals(version.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidDataException(
                $"The dependency plan requires conflicting versions of project {version.ProjectId}.");
        }

        if (items.Count >= MaximumPlanItems)
        {
            throw new InvalidDataException($"The dependency plan exceeds {MaximumPlanItems:N0} files.");
        }

        EnsureCompatible(profile, target, version);
        var file = version.PrimaryFile
            ?? throw new InvalidDataException($"{displayName} does not publish a verified JAR for this version.");
        ValidateFile(file);
        var existingPath = Path.Combine(destination, file.FileName);
        if (File.Exists(existingPath))
        {
            throw new IOException($"{file.FileName} already exists in {target.DirectoryName}. Existing files are never overwritten automatically.");
        }

        versions.Add(version.VersionId);
        projects.Add(version.ProjectId, version.VersionId);
        items.Add(new ServerContentInstallPlanItem(
            version.ProjectId,
            version.VersionId,
            displayName,
            target.Kind,
            file,
            isDependency));

        foreach (var dependency in version.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dependency.DependencyType == "embedded")
            {
                continue;
            }

            var identity = dependency.ProjectId.Length > 0
                ? dependency.ProjectId
                : dependency.VersionId.Length > 0
                    ? dependency.VersionId
                    : dependency.FileName;
            if (dependency.DependencyType == "optional")
            {
                warnings.Add($"Optional dependency {identity} is not installed automatically.");
                continue;
            }

            if (dependency.DependencyType == "incompatible")
            {
                warnings.Add($"The publisher marks {identity} as incompatible. Review existing local files before starting the server.");
                continue;
            }

            if (dependency.DependencyType != "required")
            {
                warnings.Add($"Dependency {identity} has an unrecognised type and was not installed.");
                continue;
            }

            ServerContentVersion dependencyVersion;
            if (dependency.VersionId.Length > 0)
            {
                dependencyVersion = await _catalogService.GetVersionAsync(
                    dependency.VersionId,
                    cancellationToken);
            }
            else if (dependency.ProjectId.Length > 0)
            {
                var candidates = await _catalogService.GetVersionsAsync(
                    dependency.ProjectId,
                    profile.MinecraftVersion,
                    target.LoaderIds,
                    cancellationToken);
                dependencyVersion = candidates.FirstOrDefault(candidate =>
                    IsCompatible(profile, target, candidate) && candidate.PrimaryFile is not null)
                    ?? throw new InvalidDataException($"Required dependency {dependency.ProjectId} has no compatible server version.");
            }
            else if (dependency.FileName.Length > 0
                && File.Exists(Path.Combine(destination, Path.GetFileName(dependency.FileName))))
            {
                warnings.Add($"Required external dependency {dependency.FileName} is already present.");
                continue;
            }
            else
            {
                throw new InvalidDataException($"Required external dependency {identity} could not be resolved automatically.");
            }

            await AddVersionAsync(
                profile,
                target,
                dependencyVersion,
                dependencyVersion.Name.Length > 0 ? dependencyVersion.Name : dependencyVersion.VersionNumber,
                isDependency: true,
                destination,
                items,
                warnings,
                projects,
                versions,
                cancellationToken);
        }
    }

    private static void EnsureCompatible(
        ServerProfile profile,
        ServerContentTarget target,
        ServerContentVersion version)
    {
        if (!IsCompatible(profile, target, version))
        {
            throw new InvalidDataException(
                $"{version.VersionNumber} is not compatible with Minecraft {profile.MinecraftVersion} and {target.LoadersText}.");
        }
    }

    private static bool IsCompatible(
        ServerProfile profile,
        ServerContentTarget target,
        ServerContentVersion version)
    {
        var minecraftMatches = profile.MinecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || version.MinecraftVersions.Contains(profile.MinecraftVersion, StringComparer.OrdinalIgnoreCase);
        var loaderMatches = target.LoaderIds.Count == 0
            || version.Loaders.Any(loader => target.LoaderIds.Contains(loader, StringComparer.OrdinalIgnoreCase));
        return minecraftMatches && loaderMatches && version.IsServerCompatible;
    }

    private static async Task DownloadAndVerifyAsync(
        ServerContentFile file,
        string destination,
        long completedBefore,
        long totalBytes,
        IProgress<ServerContentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            file.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != file.Size)
        {
            throw new InvalidDataException($"{file.FileName} has a different size than the publisher declared.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long downloaded = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                downloaded += count;
                if (downloaded > MaximumFileLength || downloaded > file.Size)
                {
                    throw new InvalidDataException($"{file.FileName} exceeded its declared size.");
                }

                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                progress?.Report(new ServerContentInstallProgress(
                    ServerContentInstallStage.Downloading,
                    $"Downloading {file.FileName}…",
                    completedBefore + downloaded,
                    totalBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (downloaded != file.Size)
        {
            throw new InvalidDataException($"{file.FileName} did not match its declared size.");
        }

        progress?.Report(new ServerContentInstallProgress(
            ServerContentInstallStage.Verifying,
            $"Verifying {file.FileName}…",
            completedBefore + downloaded,
            totalBytes));
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{file.FileName} failed SHA-512 verification.");
        }
    }

    private static void ValidateFile(ServerContentFile file)
    {
        if (file.Size <= 0 || file.Size > MaximumFileLength)
        {
            throw new InvalidDataException($"{file.FileName} has an invalid or unsafe download size.");
        }

        if (!file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(file.FileName).Equals(file.FileName, StringComparison.Ordinal)
            || file.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !file.DownloadUri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
            || file.Sha512.Length != 128
            || !file.Sha512.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{file.FileName} does not have safe verified download metadata.");
        }
    }

    private static void EnsureNoCollisions(
        string destination,
        IReadOnlyList<ServerContentInstallPlanItem> items)
    {
        foreach (var item in items)
        {
            if (File.Exists(Path.Combine(destination, item.File.FileName)))
            {
                throw new IOException($"{item.File.FileName} already exists. Existing server content is never overwritten automatically.");
            }
        }
    }

    internal static string ResolveContainedDirectory(string serverRoot, string directoryName)
    {
        if (directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || directoryName is "." or ".."
            || Path.IsPathRooted(directoryName))
        {
            throw new InvalidDataException("The content directory name is invalid.");
        }

        return EnsureContainedPath(serverRoot, Path.Combine(serverRoot, directoryName));
    }

    private static string EnsureContainedPath(string serverRoot, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(serverRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The content path escapes the selected server directory.");
        }

        return normalizedCandidate;
    }

    private static void EnsureNotLink(string path, string description)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException($"The {description} is a link and cannot be changed safely.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.2"));
        return client;
    }
}
