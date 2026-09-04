using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class PackDependencyResolver : IPackDependencyResolver
{
    private readonly IPackContentCatalogService _catalogService;

    public PackDependencyResolver(IPackContentCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public async Task<PackResolutionPlan> ResolveAsync(
        PackResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = new ResolutionState(request);
        await AddVersionAsync(
            request.Project,
            request.Version,
            request.RootIsDependency,
            request.RootDependencyType,
            request.RootReason,
            string.Empty,
            string.Empty,
            state,
            cancellationToken);
        ValidateIncompatibilities(state);
        return new PackResolutionPlan(state.Items, state.Warnings, state.Conflicts)
        {
            OptionalDependencies = state.OptionalDependencies
        };
    }

    private async Task AddVersionAsync(
        ServerContentProject? project,
        ServerContentVersion version,
        bool isDependency,
        string dependencyType,
        string reason,
        string displayNameOverride,
        string iconUrl,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedDisplayName = DisplayName(project, version, displayNameOverride);
        var identity = Identity(version.ProviderId, version.VersionId);
        if (!state.VisitedVersions.Add(identity))
        {
            return;
        }

        var projectIdentity = Identity(version.ProviderId, version.ProjectId);
        var existingProject = state.Request.ExistingItems.FirstOrDefault(item =>
            Identity(item.ProviderId, item.ProjectId) == projectIdentity);
        if (existingProject is not null)
        {
            if (!existingProject.VersionId.Equals(version.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                state.Conflicts.Add(
                    $"{resolvedDisplayName} requires {version.VersionNumber}, but {existingProject.VersionNumber} is already in the draft.");
            }
            else if (!isDependency)
            {
                state.Warnings.Add($"{resolvedDisplayName} is already in the draft.");
            }

            return;
        }

        var plannedProject = state.Items.FirstOrDefault(item =>
            Identity(item.ProviderId, item.ProjectId) == projectIdentity);
        if (plannedProject is not null)
        {
            if (!plannedProject.VersionId.Equals(version.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                state.Conflicts.Add(
                    $"Two versions of {resolvedDisplayName} are required: {plannedProject.VersionNumber} and {version.VersionNumber}.");
            }

            return;
        }

        if (!IsMinecraftCompatible(version, state.Request.MinecraftVersion))
        {
            state.Conflicts.Add(
                $"{resolvedDisplayName} {version.VersionNumber} does not declare Minecraft {state.Request.MinecraftVersion} compatibility.");
            return;
        }

        var contentKind = project?.Kind ?? InferDependencyKind(version, state.Request.Project.Kind);
        var placement = DeterminePlacement(
            version,
            contentKind,
            state.Request.Target,
            state.Request.ClientLoaderIds,
            state.Request.ServerLoaderIds,
            out var placementWarning);
        if (placement is null)
        {
            state.Conflicts.Add(
                $"{resolvedDisplayName} {version.VersionNumber} does not match the selected client or server platform.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(placementWarning))
        {
            state.Warnings.Add($"{resolvedDisplayName}: {placementWarning}");
        }

        var item = new PackDraftItem(
            version.ProviderId,
            version.ProjectId,
            version.VersionId,
            resolvedDisplayName,
            version.VersionNumber,
            contentKind,
            placement.Value,
            isDependency,
            reason)
        {
            IconUrl = !string.IsNullOrWhiteSpace(project?.IconUrl) ? project.IconUrl : iconUrl,
            DependencyType = dependencyType,
            IsExplicitSelection = !isDependency || dependencyType == "optional"
        };
        state.Items.Add(item);

        foreach (var dependency in version.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencyLabel = DependencyLabel(dependency);
            switch (dependency.DependencyType)
            {
                case "required":
                {
                    var dependencyVersion = await ResolveDependencyVersionAsync(
                        version.ProviderId,
                        dependency,
                        state,
                        cancellationToken);
                    if (dependencyVersion is null)
                    {
                        state.Conflicts.Add(
                            $"The required dependency {dependencyLabel} for {item.DisplayName} has no compatible published version.");
                        break;
                    }

                    await AddVersionAsync(
                        null,
                        dependencyVersion,
                        true,
                        "required",
                        $"Required by {item.DisplayName}",
                        dependency.DisplayName,
                        dependency.IconUrl,
                        state,
                        cancellationToken);
                    break;
                }
                case "optional":
                {
                    if (ContainsDependency(dependency, version.ProviderId, state))
                    {
                        break;
                    }

                    var optionalVersion = await ResolveDependencyVersionAsync(
                        version.ProviderId,
                        dependency,
                        state,
                        cancellationToken);
                    if (optionalVersion is null)
                    {
                        state.Warnings.Add(
                            $"{item.DisplayName} declares optional dependency {dependencyLabel}, but no compatible published version was found.");
                        break;
                    }

                    var optionalKind = InferDependencyKind(optionalVersion, contentKind);
                    var optionalPlacement = DeterminePlacement(
                        optionalVersion,
                        optionalKind,
                        state.Request.Target,
                        state.Request.ClientLoaderIds,
                        state.Request.ServerLoaderIds,
                        out var optionalWarning);
                    if (optionalPlacement is null)
                    {
                        state.Warnings.Add(
                            $"Optional dependency {DisplayName(null, optionalVersion, dependency.DisplayName)} does not match the selected client or server platform.");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(optionalWarning))
                    {
                        state.Warnings.Add($"{DisplayName(null, optionalVersion, dependency.DisplayName)}: {optionalWarning}");
                    }

                    if (!state.OptionalDependencies.Any(choice =>
                            choice.ProviderId.Equals(optionalVersion.ProviderId, StringComparison.OrdinalIgnoreCase)
                            && choice.ProjectId.Equals(optionalVersion.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    {
                        state.OptionalDependencies.Add(new PackOptionalDependencyChoice(
                            item.DisplayName,
                            optionalKind,
                            optionalVersion,
                            optionalPlacement.Value)
                        {
                            ProjectDisplayName = dependency.DisplayName,
                            IconUrl = dependency.IconUrl
                        });
                    }

                    break;
                }
                case "incompatible":
                    state.Incompatibilities.Add(new IncompatibilityRule(
                        item.DisplayName,
                        version.ProviderId,
                        dependency,
                        dependencyLabel));
                    break;
            }
        }
    }

    private async Task<ServerContentVersion?> ResolveDependencyVersionAsync(
        string providerId,
        ServerContentDependency dependency,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            try
            {
                return await _catalogService.GetVersionAsync(
                    providerId,
                    dependency.VersionId,
                    cancellationToken);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(dependency.ProjectId))
        {
            return null;
        }

        var loaderIds = state.Request.ClientLoaderIds
            .Concat(state.Request.ServerLoaderIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var versions = await _catalogService.GetVersionsAsync(
            providerId,
            dependency.ProjectId,
            state.Request.MinecraftVersion,
            loaderIds,
            cancellationToken);
        return versions
            .Where(version => IsMinecraftCompatible(version, state.Request.MinecraftVersion))
            .OrderBy(version => ReleaseOrder(version.ReleaseChannel))
            .ThenByDescending(version => version.PublishedAt)
            .FirstOrDefault(version => DeterminePlacement(
                version,
                state.Request.Project.Kind,
                state.Request.Target,
                state.Request.ClientLoaderIds,
                state.Request.ServerLoaderIds,
                out _) is not null);
    }

    internal static PackContentPlacement? DeterminePlacement(
        ServerContentVersion version,
        ServerContentKind kind,
        PackBuildTarget target,
        IReadOnlyList<string> clientLoaderIds,
        IReadOnlyList<string> serverLoaderIds,
        out string warning)
    {
        warning = string.Empty;
        var clientMatches = kind == ServerContentKind.Mod
            && TargetIncludesClient(target)
            && MatchesLoader(version, clientLoaderIds);
        var serverMatches = TargetIncludesServer(target)
            && MatchesLoader(version, serverLoaderIds);

        var environment = version.Environment.Trim().ToLowerInvariant();
        switch (environment)
        {
            case "client_only":
            case "singleplayer_only":
                serverMatches = false;
                break;
            case "dedicated_server_only":
            case "server_only":
                clientMatches = false;
                break;
            case "client_only_server_optional":
                serverMatches = false;
                warning = "This is client-first content. The optional server copy was left out for review.";
                break;
            case "server_only_client_optional":
                clientMatches = false;
                warning = "This is server-first content. The optional client copy was left out for review.";
                break;
        }

        if (kind == ServerContentKind.Plugin)
        {
            clientMatches = false;
        }

        var knownEnvironment = environment is
            "client_only" or
            "singleplayer_only" or
            "dedicated_server_only" or
            "server_only" or
            "client_only_server_optional" or
            "server_only_client_optional" or
            "client_and_server" or
            "client_or_server" or
            "client_or_server_prefers_both";
        if (!knownEnvironment)
        {
            warning = "The provider did not declare a recognised side. Confirm its placement before installation.";
            if (target == PackBuildTarget.ClientAndServer && clientMatches && serverMatches)
            {
                return PackContentPlacement.Review;
            }
        }

        if (clientMatches && serverMatches)
        {
            return PackContentPlacement.Both;
        }

        if (clientMatches)
        {
            return PackContentPlacement.Client;
        }

        if (serverMatches)
        {
            return PackContentPlacement.Server;
        }

        return null;
    }

    private static bool MatchesLoader(
        ServerContentVersion version,
        IReadOnlyList<string> selectedLoaderIds)
    {
        if (selectedLoaderIds.Count == 0)
        {
            return false;
        }

        return version.Loaders.Count == 0 || version.Loaders.Any(loader =>
            selectedLoaderIds.Contains(loader, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsMinecraftCompatible(ServerContentVersion version, string minecraftVersion) =>
        string.IsNullOrWhiteSpace(minecraftVersion)
        || minecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || version.MinecraftVersions.Count == 0
        || version.MinecraftVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase);

    private static bool TargetIncludesClient(PackBuildTarget target) =>
        target is PackBuildTarget.Client or PackBuildTarget.ClientAndServer;

    private static bool TargetIncludesServer(PackBuildTarget target) =>
        target is PackBuildTarget.Server or PackBuildTarget.ClientAndServer;

    private static string DisplayName(
        ServerContentProject? project,
        ServerContentVersion version,
        string displayNameOverride) =>
        !string.IsNullOrWhiteSpace(project?.Title)
            ? project.Title
            : !string.IsNullOrWhiteSpace(displayNameOverride)
                ? displayNameOverride
                : !string.IsNullOrWhiteSpace(version.Name)
                    ? version.Name
                    : version.ProjectId;

    private static string DependencyLabel(ServerContentDependency dependency) =>
        !string.IsNullOrWhiteSpace(dependency.DisplayName)
            ? dependency.DisplayName
            : !string.IsNullOrWhiteSpace(dependency.FileName)
                ? dependency.FileName
                : !string.IsNullOrWhiteSpace(dependency.ProjectId)
                    ? $"project {dependency.ProjectId}"
                    : !string.IsNullOrWhiteSpace(dependency.VersionId)
                ? dependency.VersionId
                : "an unspecified project";

    private static int ReleaseOrder(string releaseChannel) => releaseChannel switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

    private static ServerContentKind InferDependencyKind(
        ServerContentVersion version,
        ServerContentKind parentKind) =>
        version.Loaders.Any(loader => loader is "paper" or "spigot" or "bukkit" or "purpur")
            ? ServerContentKind.Plugin
            : parentKind;

    private static bool ContainsDependency(
        ServerContentDependency dependency,
        string providerId,
        ResolutionState state)
    {
        bool Matches(PackDraftItem item) =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            && ((!string.IsNullOrWhiteSpace(dependency.ProjectId)
                    && item.ProjectId.Equals(dependency.ProjectId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(dependency.VersionId)
                    && item.VersionId.Equals(dependency.VersionId, StringComparison.OrdinalIgnoreCase)));

        return state.Request.ExistingItems.Any(Matches) || state.Items.Any(Matches);
    }

    private static void ValidateIncompatibilities(ResolutionState state)
    {
        foreach (var rule in state.Incompatibilities)
        {
            if (ContainsDependency(rule.Dependency, rule.ProviderId, state))
            {
                state.Conflicts.Add(
                    $"{rule.OwnerName} is incompatible with {rule.DependencyLabel}, which is already in this draft.");
            }
        }
    }

    private static string Identity(string providerId, string itemId) =>
        $"{providerId.Trim().ToLowerInvariant()}:{itemId.Trim().ToLowerInvariant()}";

    private sealed class ResolutionState
    {
        public ResolutionState(PackResolveRequest request)
        {
            Request = request;
        }

        public PackResolveRequest Request { get; }

        public List<PackDraftItem> Items { get; } = [];

        public List<string> Warnings { get; } = [];

        public List<string> Conflicts { get; } = [];

        public List<PackOptionalDependencyChoice> OptionalDependencies { get; } = [];

        public List<IncompatibilityRule> Incompatibilities { get; } = [];

        public HashSet<string> VisitedVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record IncompatibilityRule(
        string OwnerName,
        string ProviderId,
        ServerContentDependency Dependency,
        string DependencyLabel);
}
