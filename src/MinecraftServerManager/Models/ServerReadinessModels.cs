namespace MinecraftServerManager.Models;

public enum ServerReadinessState
{
    Ready,
    ReadyWithNotice,
    ActionRequired
}

public enum ServerEulaState
{
    Accepted,
    Missing,
    NotAccepted,
    InvalidOrUnreadable
}

public sealed record ServerReadinessReport(
    ServerReadinessState State,
    string Summary,
    string LauncherText,
    string JavaText,
    string MemoryText,
    ServerEulaState EulaState,
    string EulaText,
    string EulaPath,
    bool CanStart,
    string ValidationText)
{
    public string Heading => State switch
    {
        ServerReadinessState.Ready => "Ready to start",
        ServerReadinessState.ReadyWithNotice => "Ready with notes",
        _ => "Action required"
    };

    public bool NeedsEulaAcceptance => EulaState != ServerEulaState.Accepted
        && !string.IsNullOrWhiteSpace(EulaPath);
}
