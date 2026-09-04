using System.Reflection;

namespace MinecraftServerManager.Services;

public sealed class AssemblyMetadataCurseForgeApplicationApiKeyProvider
    : ICurseForgeApplicationApiKeyProvider
{
    private const string MetadataName = "CurseForgeApiKey";
    private readonly Assembly _assembly;

    public AssemblyMetadataCurseForgeApplicationApiKeyProvider()
        : this(typeof(AssemblyMetadataCurseForgeApplicationApiKeyProvider).Assembly)
    {
    }

    internal AssemblyMetadataCurseForgeApplicationApiKeyProvider(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public string? GetApiKey()
    {
        var value = _assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(
                MetadataName,
                StringComparison.Ordinal))
            ?.Value
            ?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
