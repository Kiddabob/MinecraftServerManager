namespace MinecraftServerManager.Models;

public sealed record AppThemeOption(string Id, string DisplayName);

public sealed record AccentColorOption(string Id, string DisplayName, string HexColor);

public sealed record UpdateIntervalOption(int Minutes, string DisplayName);
