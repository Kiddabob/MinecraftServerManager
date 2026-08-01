namespace MinecraftServerManager.Models;

public enum AppUpdateState
{
    Disabled,
    Checking,
    UpToDate,
    Downloading,
    ReadyToApply,
    Failed
}
