namespace MinecraftServerManager.Models;

public sealed record PlayerConnectionChange(
    string PlayerName,
    PlayerConnectionKind Kind);

public enum PlayerConnectionKind
{
    Joined,
    Left
}
