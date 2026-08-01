namespace MinecraftServerManager.Models;

public sealed class ServerOutputEventArgs : EventArgs
{
    public ServerOutputEventArgs(string line, ServerOutputStream stream)
    {
        Line = line;
        Stream = stream;
    }

    public string Line { get; }

    public ServerOutputStream Stream { get; }
}
