namespace MinecraftServerManager.Models;

public enum PackBuildTarget
{
    Client,
    Server,
    ClientAndServer
}

public enum PackPlatformKind
{
    Vanilla,
    ModLoader,
    PluginPlatform,
    HybridPlatform
}

public enum PackContentPlacement
{
    Client,
    Server,
    Both,
    Review
}

public sealed record PackBuildTargetOption(
    PackBuildTarget Target,
    string DisplayName,
    string Description);

public sealed record PackPlatformOption(
    string Id,
    string DisplayName,
    PackPlatformKind Kind,
    string BestFor,
    string Details,
    IReadOnlyList<string> LoaderIds,
    bool SupportsClient,
    bool SupportsServer,
    bool SupportsMods,
    bool SupportsPlugins,
    bool IsExperimental = false)
{
    public string KindText => Kind switch
    {
        PackPlatformKind.Vanilla => "Vanilla",
        PackPlatformKind.ModLoader => "Mod loader",
        PackPlatformKind.PluginPlatform => "Plugin platform",
        PackPlatformKind.HybridPlatform => "Hybrid platform",
        _ => "Platform"
    };

    public string CapabilityText
    {
        get
        {
            var content = SupportsMods && SupportsPlugins
                ? "Mods and plugins"
                : SupportsMods
                    ? "Mods"
                    : SupportsPlugins
                        ? "Plugins"
                        : "No add-on catalogue";
            return IsExperimental ? $"{content}  •  Experimental" : content;
        }
    }

    public string GuidanceText => $"{BestFor}  •  {Details}";
}

public sealed record PackPlatformVersionOption(
    string PlatformId,
    string LoaderId,
    string Version,
    bool IsStable,
    bool CanPrepareServer)
{
    public string StabilityText => IsStable ? "Stable" : "Preview";

    public string DisplayName => $"{Version}  •  {StabilityText}";
}

public sealed record PackCategoryOption(string Id, string DisplayName);

public sealed record PackCatalogSearchRequest(
    string Query,
    string MinecraftVersion,
    ServerContentKind Kind,
    PackBuildTarget Target,
    IReadOnlyList<string> LoaderIds,
    IReadOnlyList<string> Categories,
    int Offset = 0,
    int Limit = 20);

public sealed record PackProviderStatus(
    string ProviderId,
    string DisplayName,
    bool IsAvailable,
    int ResultCount,
    string Message)
{
    public string StatusText => IsAvailable
        ? $"{DisplayName}: {ResultCount:N0} matching project{(ResultCount == 1 ? string.Empty : "s")}"
        : $"{DisplayName}: unavailable — {Message}";
}

public sealed record PackCatalogSearchPage(
    IReadOnlyList<ServerContentProject> Items,
    IReadOnlyList<PackProviderStatus> Providers)
{
    public int AvailableProviderCount => Providers.Count(provider => provider.IsAvailable);

    public string ProviderSummary => Providers.Count == 0
        ? "No catalogue providers are configured."
        : $"{AvailableProviderCount:N0} of {Providers.Count:N0} supported provider{(Providers.Count == 1 ? string.Empty : "s")} responded";
}

public sealed record PackResolveRequest(
    PackBuildTarget Target,
    string MinecraftVersion,
    IReadOnlyList<string> ClientLoaderIds,
    IReadOnlyList<string> ServerLoaderIds,
    ServerContentProject Project,
    ServerContentVersion Version,
    IReadOnlyList<PackDraftItem> ExistingItems);

public sealed record PackDraftItem(
    string ProviderId,
    string ProjectId,
    string VersionId,
    string DisplayName,
    string VersionNumber,
    ServerContentKind Kind,
    PackContentPlacement Placement,
    bool IsDependency,
    string Reason)
{
    public string PlacementText => Placement switch
    {
        PackContentPlacement.Client => "Client",
        PackContentPlacement.Server => "Server",
        PackContentPlacement.Both => "Client + server",
        _ => "Needs review"
    };

    public string SourceText => $"{ProviderId}  •  {VersionNumber}";

    public string DetailsText => IsDependency
        ? $"Required dependency  •  {PlacementText}"
        : $"Selected content  •  {PlacementText}";
}

public sealed record PackResolutionPlan(
    IReadOnlyList<PackDraftItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Conflicts)
{
    public bool IsReady => Conflicts.Count == 0 && Items.Count > 0;

    public string SummaryText => Conflicts.Count > 0
        ? $"Resolve {Conflicts.Count:N0} conflict{(Conflicts.Count == 1 ? string.Empty : "s")} before adding this item."
        : Items.Count == 1
            ? $"Add {Items[0].DisplayName} to the draft."
            : $"Add {Items[0].DisplayName} and {Items.Count - 1:N0} required dependenc{(Items.Count == 2 ? "y" : "ies")} to the draft.";
}
