namespace MinecraftServerManager.Models;

public sealed class AppUpdateStatusChangedEventArgs : EventArgs
{
    public AppUpdateStatusChangedEventArgs(AppUpdateState state, string message, int? progressPercent = null)
    {
        State = state;
        Message = message;
        ProgressPercent = progressPercent;
    }

    public AppUpdateState State { get; }

    public string Message { get; }

    public int? ProgressPercent { get; }
}
