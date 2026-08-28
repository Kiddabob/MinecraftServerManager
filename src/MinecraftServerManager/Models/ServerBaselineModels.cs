namespace MinecraftServerManager.Models;

public sealed record ServerBaselineInstallRequest(
    string MinecraftVersion,
    string LoaderId,
    string LoaderVersion,
    string ServerDirectory);

public sealed record ServerBaselineInstallResult(
    bool WasInstalled,
    string LauncherFileName,
    string Message);
