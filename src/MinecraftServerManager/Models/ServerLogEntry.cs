namespace MinecraftServerManager.Models;

public sealed record ServerLogEntry(
    string TimeText,
    ServerLogLevel Level,
    string Source,
    string Message)
{
    public string LevelText => Level switch
    {
        ServerLogLevel.Information => "Info",
        ServerLogLevel.Warning => "Warning",
        ServerLogLevel.Error => "Error",
        ServerLogLevel.Success => "Ready",
        ServerLogLevel.Command => "Command",
        ServerLogLevel.Manager => "Manager",
        _ => Level.ToString()
    };

    public string Glyph => Level switch
    {
        ServerLogLevel.Information => "\uE946",
        ServerLogLevel.Warning => "\uE7BA",
        ServerLogLevel.Error => "\uE783",
        ServerLogLevel.Success => "\uE73E",
        ServerLogLevel.Command => "\uE756",
        ServerLogLevel.Manager => "\uE713",
        _ => "\uE946"
    };
}
