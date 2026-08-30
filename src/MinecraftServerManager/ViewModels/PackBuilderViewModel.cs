using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class PackBuilderViewModel : BindableBase
{
    private readonly IPackContentCatalogService _catalogService;
    private readonly IPackDependencyResolver _dependencyResolver;
    private readonly IPackPlatformCatalogService _platformCatalog;
    private PackBuildTargetOption? _selectedTarget;
    private string? _selectedMinecraftVersion;
    private PackPlatformOption? _selectedClientPlatform;
    private PackPlatformOption? _selectedServerPlatform;
    private ServerContentKindOption? _selectedContentKind;
    private PackCategoryOption? _selectedCategory;
    private ServerContentProject? _selectedProject;
    private ServerContentVersion? _selectedVersion;
    private string _searchText = string.Empty;
    private string _statusText = "Choose what to build, then select a Minecraft version and platform.";
    private string _versionStatusText = "Select a search result to inspect all compatible published versions.";
    private string _draftStatusText = "Planning only — no files are downloaded or changed.";
    private bool _isBusy;
    private bool _isLoaded;
    private int _busyOperationCount;
    private int _versionRequestId;

    public PackBuilderViewModel(
        IPackContentCatalogService catalogService,
        IPackDependencyResolver dependencyResolver,
        IPackPlatformCatalogService platformCatalog)
    {
        _catalogService = catalogService;
        _dependencyResolver = dependencyResolver;
        _platformCatalog = platformCatalog;

        foreach (var option in platformCatalog.GetBuildTargets())
        {
            BuildTargets.Add(option);
        }

        foreach (var option in platformCatalog.GetClientPlatforms())
        {
            ClientPlatforms.Add(option);
        }

        foreach (var option in platformCatalog.GetServerPlatforms())
        {
            ServerPlatforms.Add(option);
        }

        _selectedTarget = BuildTargets.First(option => option.Target == PackBuildTarget.ClientAndServer);
        _selectedClientPlatform = ClientPlatforms.First(option => option.Id == "fabric-client");
        _selectedServerPlatform = ServerPlatforms.First(option => option.Id == "fabric-server");
        RefreshContentKinds();

        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        ClearDraftCommand = new AsyncRelayCommand(ClearDraftAsync, () => DraftItems.Count > 0 && !IsBusy);
    }

    public ObservableCollection<PackBuildTargetOption> BuildTargets { get; } = [];

    public ObservableCollection<string> MinecraftVersions { get; } = [];

    public ObservableCollection<PackPlatformOption> ClientPlatforms { get; } = [];

    public ObservableCollection<PackPlatformOption> ServerPlatforms { get; } = [];

    public ObservableCollection<ServerContentKindOption> ContentKinds { get; } = [];

    public ObservableCollection<PackCategoryOption> Categories { get; } = [];

    public ObservableCollection<PackProviderStatus> ProviderStatuses { get; } = [];

    public ObservableCollection<ServerContentProject> SearchResults { get; } = [];

    public ObservableCollection<ServerContentVersion> Versions { get; } = [];

    public ObservableCollection<PackDraftItem> DraftItems { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand ClearDraftCommand { get; }

    public PackBuildTargetOption? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowClientPlatform));
            OnPropertyChanged(nameof(ShowServerPlatform));
            OnPropertyChanged(nameof(TargetGuidance));
            RefreshContentKinds();
            EnsureContentKindSupported();
            ResetForSetupChange("Build target changed. Search again to create a compatible draft.");
        }
    }

    public string? SelectedMinecraftVersion
    {
        get => _selectedMinecraftVersion;
        set
        {
            if (SetProperty(ref _selectedMinecraftVersion, value))
            {
                ResetForSetupChange("Minecraft version changed. Search again to create a compatible draft.");
            }
        }
    }

    public PackPlatformOption? SelectedClientPlatform
    {
        get => _selectedClientPlatform;
        set
        {
            if (SetProperty(ref _selectedClientPlatform, value))
            {
                OnPropertyChanged(nameof(ClientPlatformGuidance));
                EnsureContentKindSupported();
                ResetForSetupChange("Client platform changed. Search again to check every selected item.");
            }
        }
    }

    public PackPlatformOption? SelectedServerPlatform
    {
        get => _selectedServerPlatform;
        set
        {
            if (SetProperty(ref _selectedServerPlatform, value))
            {
                OnPropertyChanged(nameof(ServerPlatformGuidance));
                EnsureContentKindSupported();
                ResetForSetupChange("Server platform changed. Search again to check every selected item.");
            }
        }
    }

    public ServerContentKindOption? SelectedContentKind
    {
        get => _selectedContentKind;
        set
        {
            if (!SetProperty(ref _selectedContentKind, value))
            {
                return;
            }

            RefreshCategories();
            ClearSearchSelection();
            StatusText = value?.Kind == ServerContentKind.Plugin
                ? "Plugin results target the selected server platform and are never placed on the client."
                : "Mod results are checked against the selected client and server loaders.";
            NotifyCommandStates();
        }
    }

    public PackCategoryOption? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    public ServerContentProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            Versions.Clear();
            _selectedVersion = null;
            OnPropertyChanged(nameof(SelectedVersion));
            OnPropertyChanged(nameof(HasSelectedProject));
            OnPropertyChanged(nameof(HasSelectedVersion));
            NotifyCommandStates();
            _ = LoadVersionsAsync(value);
        }
    }

    public ServerContentVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(HasSelectedVersion));
                OnPropertyChanged(nameof(SelectedVersionDetails));
                NotifyCommandStates();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string VersionStatusText
    {
        get => _versionStatusText;
        private set => SetProperty(ref _versionStatusText, value);
    }

    public string DraftStatusText
    {
        get => _draftStatusText;
        private set => SetProperty(ref _draftStatusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                NotifyCommandStates();
            }
        }
    }

    public bool ShowClientPlatform => SelectedTarget?.Target is
        PackBuildTarget.Client or PackBuildTarget.ClientAndServer;

    public bool ShowServerPlatform => SelectedTarget?.Target is
        PackBuildTarget.Server or PackBuildTarget.ClientAndServer;

    public bool IsNotBusy => !IsBusy;

    public bool HasSelectedProject => SelectedProject is not null;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool CanReviewAdd => SelectedProject is not null && SelectedVersion is not null && !IsBusy;

    public string TargetGuidance => SelectedTarget?.Description ?? "Choose an output.";

    public string ClientPlatformGuidance => SelectedClientPlatform is null
        ? "Choose a client loader."
        : $"{SelectedClientPlatform.KindText}  •  {SelectedClientPlatform.CapabilityText}\n{SelectedClientPlatform.GuidanceText}";

    public string ServerPlatformGuidance => SelectedServerPlatform is null
        ? "Choose a server platform."
        : $"{SelectedServerPlatform.KindText}  •  {SelectedServerPlatform.CapabilityText}\n{SelectedServerPlatform.GuidanceText}";

    public string SelectedVersionDetails => SelectedVersion is null
        ? "Choose a published version."
        : $"{SelectedVersion.CompatibilityText}\n{SelectedVersion.DependencyText}";

    public string DraftCountText => DraftItems.Count == 1
        ? "1 planned item"
        : $"{DraftItems.Count:N0} planned items";

    public void EnsureLoaded()
    {
        if (!_isLoaded)
        {
            _ = LoadMinecraftVersionsAsync();
        }
    }

    public async Task<PackResolutionPlan?> PrepareAddAsync()
    {
        var project = SelectedProject;
        var version = SelectedVersion;
        var target = SelectedTarget;
        if (!CanReviewAdd || project is null || version is null || target is null)
        {
            return null;
        }

        BeginBusy();
        DraftStatusText = "Resolving required dependencies and checking side placement…";
        try
        {
            var plan = await _dependencyResolver.ResolveAsync(new PackResolveRequest(
                target.Target,
                SelectedMinecraftVersion ?? string.Empty,
                GetClientLoaderIds(),
                GetServerLoaderIds(project.Kind),
                project,
                version,
                DraftItems.ToArray()));
            DraftStatusText = plan.SummaryText;
            return plan;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
            or InvalidOperationException or JsonException or TaskCanceledException)
        {
            DraftStatusText = $"The compatibility plan could not be created: {exception.Message}";
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    public void CommitPlan(PackResolutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsReady)
        {
            DraftStatusText = plan.SummaryText;
            return;
        }

        foreach (var item in plan.Items)
        {
            if (!DraftItems.Any(existing =>
                existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                && existing.VersionId.Equals(item.VersionId, StringComparison.OrdinalIgnoreCase)))
            {
                DraftItems.Add(item);
            }
        }

        DraftStatusText = $"{DraftCountText}. Draft only — nothing has been downloaded or installed.";
        OnPropertyChanged(nameof(DraftCountText));
        ClearDraftCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadMinecraftVersionsAsync()
    {
        if (_isLoaded || IsBusy)
        {
            return;
        }

        BeginBusy();
        StatusText = "Loading supported Minecraft releases from configured providers…";
        try
        {
            var versions = await _catalogService.GetMinecraftVersionsAsync();
            MinecraftVersions.Clear();
            foreach (var version in versions)
            {
                MinecraftVersions.Add(version);
            }

            _selectedMinecraftVersion = MinecraftVersions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedMinecraftVersion));
            _isLoaded = versions.Count > 0;
            StatusText = versions.Count == 0
                ? "No provider returned Minecraft release metadata. Try again when online."
                : $"Choose filters and search {versions.Count:N0} known Minecraft releases across all supported providers.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
            or JsonException or TaskCanceledException)
        {
            StatusText = $"Minecraft releases could not be loaded: {exception.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task SearchAsync()
    {
        if (!CanSearch() || SelectedTarget is null || SelectedContentKind?.Kind is not { } kind)
        {
            return;
        }

        BeginBusy();
        ClearSearchSelection();
        ProviderStatuses.Clear();
        StatusText = "Searching every configured provider independently…";
        try
        {
            var category = string.IsNullOrWhiteSpace(SelectedCategory?.Id)
                ? Array.Empty<string>()
                : new[] { SelectedCategory.Id };
            var page = await _catalogService.SearchAsync(new PackCatalogSearchRequest(
                SearchText,
                SelectedMinecraftVersion ?? string.Empty,
                kind,
                SelectedTarget.Target,
                GetSearchLoaderIds(kind),
                category));
            foreach (var provider in page.Providers)
            {
                ProviderStatuses.Add(provider);
            }

            foreach (var item in page.Items)
            {
                SearchResults.Add(item);
            }

            StatusText = page.Items.Count == 0
                ? $"No compatible results. {page.ProviderSummary}. Try a broader category or another platform."
                : $"{page.Items.Count:N0} compatible result{(page.Items.Count == 1 ? string.Empty : "s")} shown  •  {page.ProviderSummary}.";
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task LoadVersionsAsync(ServerContentProject? project)
    {
        var requestId = Interlocked.Increment(ref _versionRequestId);
        if (project is null || string.IsNullOrWhiteSpace(SelectedMinecraftVersion))
        {
            VersionStatusText = "Select a search result to inspect all compatible published versions.";
            return;
        }

        BeginBusy();
        VersionStatusText = "Checking every compatible published version…";
        try
        {
            var versions = await _catalogService.GetVersionsAsync(
                project.ProviderId,
                project.ProjectId,
                SelectedMinecraftVersion,
                GetSearchLoaderIds(project.Kind));
            if (requestId != _versionRequestId || !ReferenceEquals(project, SelectedProject))
            {
                return;
            }

            Versions.Clear();
            foreach (var version in versions)
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault();
            VersionStatusText = versions.Count == 0
                ? "No downloadable version matches the selected Minecraft version and platform."
                : $"Checked {versions.Count:N0} compatible published version{(versions.Count == 1 ? string.Empty : "s")}. A release is preferred, but you can choose another.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
            or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if (requestId == _versionRequestId)
            {
                VersionStatusText = $"Versions could not be loaded: {exception.Message}";
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private bool CanSearch()
    {
        if (IsBusy || SelectedTarget is null || string.IsNullOrWhiteSpace(SelectedMinecraftVersion)
            || SelectedContentKind?.Kind is not { } kind)
        {
            return false;
        }

        if (kind == ServerContentKind.Plugin)
        {
            return ShowServerPlatform && SelectedServerPlatform?.SupportsPlugins == true;
        }

        return (ShowClientPlatform && SelectedClientPlatform?.SupportsMods == true)
            || (ShowServerPlatform && SelectedServerPlatform?.SupportsMods == true);
    }

    private IReadOnlyList<string> GetSearchLoaderIds(ServerContentKind kind) =>
        kind == ServerContentKind.Plugin
            ? GetServerLoaderIds(kind)
            : GetClientLoaderIds()
                .Concat(GetServerLoaderIds(kind))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private IReadOnlyList<string> GetClientLoaderIds() =>
        ShowClientPlatform && SelectedClientPlatform?.SupportsMods == true
            ? SelectedClientPlatform.LoaderIds
            : [];

    private IReadOnlyList<string> GetServerLoaderIds(ServerContentKind kind) =>
        ShowServerPlatform
        && SelectedServerPlatform is { } platform
        && (kind == ServerContentKind.Mod ? platform.SupportsMods : platform.SupportsPlugins)
            ? platform.LoaderIds
            : [];

    private void RefreshCategories()
    {
        Categories.Clear();
        var kind = SelectedContentKind?.Kind ?? ServerContentKind.Mod;
        foreach (var category in _platformCatalog.GetCategories(kind))
        {
            Categories.Add(category);
        }

        _selectedCategory = Categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private void RefreshContentKinds()
    {
        var preferredKind = _selectedContentKind?.Kind ?? ServerContentKind.Mod;
        ContentKinds.Clear();
        ContentKinds.Add(new ServerContentKindOption("mod", "Mods", ServerContentKind.Mod));
        if (ShowServerPlatform)
        {
            ContentKinds.Add(new ServerContentKindOption("plugin", "Plugins", ServerContentKind.Plugin));
        }

        _selectedContentKind = ContentKinds.FirstOrDefault(option => option.Kind == preferredKind)
            ?? ContentKinds[0];
        OnPropertyChanged(nameof(SelectedContentKind));
        RefreshCategories();
    }

    private void EnsureContentKindSupported()
    {
        if (SelectedContentKind?.Kind == ServerContentKind.Plugin
            && (!ShowServerPlatform || SelectedServerPlatform?.SupportsPlugins != true))
        {
            _selectedContentKind = ContentKinds.First(option => option.Kind == ServerContentKind.Mod);
            OnPropertyChanged(nameof(SelectedContentKind));
            RefreshCategories();
        }

        if (SelectedContentKind?.Kind == ServerContentKind.Mod
            && (!ShowClientPlatform || SelectedClientPlatform?.SupportsMods != true)
            && (!ShowServerPlatform || SelectedServerPlatform?.SupportsMods != true)
            && ShowServerPlatform
            && SelectedServerPlatform?.SupportsPlugins == true
            && ContentKinds.FirstOrDefault(option => option.Kind == ServerContentKind.Plugin) is { } plugins)
        {
            _selectedContentKind = plugins;
            OnPropertyChanged(nameof(SelectedContentKind));
            RefreshCategories();
        }
    }

    private void ResetForSetupChange(string message)
    {
        ClearSearchSelection();
        if (DraftItems.Count > 0)
        {
            DraftItems.Clear();
            OnPropertyChanged(nameof(DraftCountText));
            ClearDraftCommand.NotifyCanExecuteChanged();
            DraftStatusText = "The previous draft was cleared because its compatibility context changed.";
        }

        StatusText = message;
        NotifyCommandStates();
    }

    private void ClearSearchSelection()
    {
        Interlocked.Increment(ref _versionRequestId);
        SearchResults.Clear();
        Versions.Clear();
        _selectedProject = null;
        _selectedVersion = null;
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(SelectedVersionDetails));
        NotifyCommandStates();
    }

    private Task ClearDraftAsync()
    {
        DraftItems.Clear();
        OnPropertyChanged(nameof(DraftCountText));
        DraftStatusText = "Draft cleared. No files were changed.";
        ClearDraftCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private void BeginBusy()
    {
        Interlocked.Increment(ref _busyOperationCount);
        IsBusy = true;
    }

    private void EndBusy()
    {
        if (Interlocked.Decrement(ref _busyOperationCount) <= 0)
        {
            Interlocked.Exchange(ref _busyOperationCount, 0);
            IsBusy = false;
        }
    }

    private void NotifyCommandStates()
    {
        SearchCommand.NotifyCanExecuteChanged();
        ClearDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanReviewAdd));
    }
}
