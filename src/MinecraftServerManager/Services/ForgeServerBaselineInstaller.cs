using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ForgeServerBaselineInstaller : IServerBaselineInstaller
{
    private const string MavenHost = "maven.minecraftforge.net";
    private static readonly Uri MetadataUri = new(
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
    private readonly IJavaRuntimeService _javaRuntimeService;

    public ForgeServerBaselineInstaller(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public bool CanInstall(string loaderId) =>
        loaderId.Equals("forge", StringComparison.OrdinalIgnoreCase);

    public async Task<ServerBaselineInstallResult> InstallAsync(
        ServerBaselineInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanInstall(request.LoaderId))
        {
            throw new NotSupportedException($"This installer does not support '{request.LoaderId}'.");
        }

        var serverDirectory = Path.GetFullPath(request.ServerDirectory);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException($"The staged server folder does not exist: {serverDirectory}");
        }

        if (ServerFolderDetector.Detect(serverDirectory) is { ServerType: "Forge" } existing)
        {
            return new ServerBaselineInstallResult(
                false,
                existing.ServerJar,
                "The pack already supplied a runnable Forge server launcher.");
        }

        progress?.Report("Resolving the exact Forge installer from the official Maven repository…");
        var metadata = await JavaServerInstallerUtilities.DownloadMavenMetadataAsync(
            MetadataUri,
            MavenHost,
            cancellationToken);
        var artifactVersion = JavaServerInstallerUtilities.ResolveForgeArtifactVersion(
            JavaServerInstallerUtilities.ParseMavenVersions(metadata),
            request.MinecraftVersion,
            request.LoaderVersion);
        var escapedVersion = Uri.EscapeDataString(artifactVersion);
        var installerFileName = $"forge-{artifactVersion}-installer.jar";
        var installerUri = new Uri(
            $"https://{MavenHost}/net/minecraftforge/forge/{escapedVersion}/{Uri.EscapeDataString(installerFileName)}");
        var installerPath = Path.Combine(serverDirectory, installerFileName);
        try
        {
            await JavaServerInstallerUtilities.DownloadVerifiedInstallerAsync(
                installerUri,
                installerPath,
                MavenHost,
                progress,
                cancellationToken);
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                request.MinecraftVersion,
                cancellationToken);
            progress?.Report(
                $"Installing Forge {request.LoaderVersion} for Minecraft {request.MinecraftVersion}…");
            await JavaServerInstallerUtilities.RunInstallerAsync(
                javaExecutable,
                installerPath,
                serverDirectory,
                ["--installServer"],
                progress,
                cancellationToken);
            JavaServerInstallerUtilities.ValidateInstalledServer(serverDirectory, "Forge");
            var detection = ServerFolderDetector.Detect(serverDirectory)!;
            return new ServerBaselineInstallResult(
                true,
                detection.ServerJar,
                $"Installed Forge {request.LoaderVersion} from the official checksum-verified installer.");
        }
        finally
        {
            JavaServerInstallerUtilities.TryDeleteFile(installerPath);
        }
    }
}
