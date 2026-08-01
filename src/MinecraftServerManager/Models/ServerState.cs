namespace MinecraftServerManager.Models;

public enum ServerState
{
    LoadingProfile,
    InvalidProfile,
    Stopped,
    Starting,
    Running,
    Ready,
    Stopping,
    Failed
}
