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

public sealed record PackCatalogSortOption(string Id, string DisplayName);

public sealed record PackCatalogPageSizeOption(int Value, string DisplayName);

public sealed record PackCatalogSearchRequest(
    string Query,
    string MinecraftVersion,
    ServerContentKind Kind,
    PackBuildTarget Target,
    IReadOnlyList<string> LoaderIds,
    IReadOnlyList<string> Categories,
    int Offset = 0,
    int Limit = 20)
{
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    public IReadOnlyList<string> Environments { get; init; } = [];

    public string Sort { get; init; } = "relevance";
}

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

    public int TotalHits => Providers
        .Where(provider => provider.IsAvailable)
        .Sum(provider => provider.ResultCount);

    public int MaximumProviderHits => Providers
        .Where(provider => provider.IsAvailable)
        .Select(provider => provider.ResultCount)
        .DefaultIfEmpty()
        .Max();

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
    IReadOnlyList<PackDraftItem> ExistingItems)
{
    public bool RootIsDependency { get; init; }

    public string RootDependencyType { get; init; } = "selected";

    public string RootReason { get; init; } = "Selected by the user";
}

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
    public string IconUrl { get; init; } = string.Empty;

    public string DependencyType { get; init; } = IsDependency ? "required" : "selected";

    public bool IsExplicitSelection { get; init; } = !IsDependency;

    public string PlacementText => Placement switch
    {
        PackContentPlacement.Client => "Client",
        PackContentPlacement.Server => "Server",
        PackContentPlacement.Both => "Client + server",
        _ => "Needs review"
    };

    public string SourceText => $"{ProviderId}  •  {VersionNumber}";

    public string DetailsText => DependencyType switch
    {
        "required" => $"Required dependency  •  {PlacementText}",
        "optional" => $"Chosen optional dependency  •  {PlacementText}",
        _ => $"Selected content  •  {PlacementText}"
    };

    public bool CanRemove => IsExplicitSelection;
}

public sealed record PackDraftSortOption(string Id, string DisplayName);

public sealed record PackDraftDisplayItem(PackDraftItem Item, int IndentLevel)
{
    public string DisplayName => Item.DisplayName;

    public string IconUrl => Item.IconUrl;

    public string SourceText => Item.SourceText;

    public string Reason => Item.Reason;

    public string PlacementText => Item.PlacementText;

    public string DetailsText => Item.DetailsText;

    public bool CanRemove => Item.CanRemove;

    public double IndentWidth => Math.Clamp(IndentLevel, 0, 5) * 24d;
}

public sealed record PackOptionalDependencyChoice(
    string OwnerName,
    ServerContentKind Kind,
    ServerContentVersion Version,
    PackContentPlacement Placement)
{
    public string ProjectDisplayName { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public string ProviderId => Version.ProviderId;

    public string ProjectId => Version.ProjectId;

    public string VersionId => Version.VersionId;

    public string DisplayName => !string.IsNullOrWhiteSpace(ProjectDisplayName)
        ? ProjectDisplayName
        : string.IsNullOrWhiteSpace(Version.Name)
            ? Version.ProjectId
            : Version.Name;

    public string DetailsText => $"{Version.VersionNumber}  •  {Placement}  •  Optional for {OwnerName}";
}

public sealed record PackResolutionPlan(
    IReadOnlyList<PackDraftItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Conflicts)
{
    public IReadOnlyList<PackOptionalDependencyChoice> OptionalDependencies { get; init; } = [];

    public bool IsReady => Conflicts.Count == 0
        && Items.Count > 0
        && Items.All(item => item.Placement != PackContentPlacement.Review);

    public bool HasDependencyReview => Items.Any(item => item.IsDependency)
        || OptionalDependencies.Count > 0;

    public string SummaryText => Conflicts.Count > 0
        ? $"Resolve {Conflicts.Count:N0} conflict{(Conflicts.Count == 1 ? string.Empty : "s")} before adding this item."
        : CreateReadySummary();

    private string CreateReadySummary()
    {
        var requiredCount = Items.Count(item => item.DependencyType == "required");
        var optionalText = OptionalDependencies.Count == 0
            ? string.Empty
            : $" Choose from {OptionalDependencies.Count:N0} optional dependenc{(OptionalDependencies.Count == 1 ? "y" : "ies")}.";
        return requiredCount == 0
            ? $"Add {Items[0].DisplayName} to the draft.{optionalText}"
            : $"Add {Items[0].DisplayName} and {requiredCount:N0} required dependenc{(requiredCount == 1 ? "y" : "ies")} to the draft.{optionalText}";
    }
}
