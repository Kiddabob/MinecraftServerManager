using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class PackBuilderViewModel : BindableBase
{
    private static readonly string[] SimilarityCategoryPriority =
    [
        "technology",
        "magic",
        "storage",
        "transportation",
        "worldgen",
        "game-mechanics",
        "management",
        "optimization",
        "equipment",
        "food",
        "adventure",
        "social",
        "utility",
        "decoration",
        "library"
    ];

    private readonly IPackContentCatalogService _catalogService;
    private readonly IPackDependencyResolver _dependencyResolver;
    private readonly IPackPlatformCatalogService _platformCatalog;
    private readonly IPackPlatformVersionService _platformVersionService;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly ICurseForgeApiKeyService _curseForgeApiKeyService;
    private readonly IPackDraftOutputService _outputService;
    private readonly IMinecraftLauncherIntegrationService _launcherIntegrationService;
    private readonly IModpackInstallLocationService _installLocationService;
    private PackBuildTargetOption? _selectedTarget;
    private string? _selectedMinecraftVersion;
    private PackPlatformOption? _selectedClientPlatform;
    private PackPlatformOption? _selectedServerPlatform;
    private PackPlatformVersionOption? _selectedClientRuntimeVersion;
    private PackPlatformVersionOption? _selectedServerRuntimeVersion;
    private ServerContentKindOption? _selectedContentKind;
    private PackCategoryOption? _selectedCategory;
    private PackCatalogSortOption _selectedSort;
    private PackCatalogPageSizeOption _selectedPageSize;
    private ServerContentProject? _selectedProject;
    private ServerContentVersion? _selectedVersion;
    private string _searchText = string.Empty;
    private string _statusText = "Choose what to build, then select a Minecraft version and platform.";
    private string _versionStatusText = "Select a search result to inspect all compatible published versions.";
    private string _draftStatusText = "Planning only — no files are downloaded or changed.";
    private string _packName = "My Minecraft Pack";
    private string _outputStatusText =
        "Build the reviewed output when it is ready. Supported server targets include their exact official baseline.";
    private string _platformVersionStatusText =
        "Choose a Minecraft version and platform to resolve exact official loader versions.";
    private string _lastOutputDirectory = string.Empty;
    private string _launcherStatusText =
        "A built client can be registered as an isolated installation in the official Minecraft Launcher.";
    private double _outputProgressPercent;
    private bool _isOutputProgressIndeterminate = true;
    private string _curseForgeConnectionStatusText =
        "No approved CurseForge developer key is stored on this Windows account.";
    private bool _isBusy;
    private bool _isOutputBusy;
    private bool _isLauncherBusy;
    private bool _hasLauncherProfile;
    private bool _isCurseForgeConnectionBusy;
    private bool _isCurseForgeApiKeyStored;
    private bool _isResolvingPlatformVersions;
    private bool _isLoaded;
    private bool _isCurseForgeConnectionLoaded;
    private bool _isRefreshingPlatformOptions;
    private int _busyOperationCount;
    private int _versionRequestId;
    private int _platformVersionRequestId;
    private int _currentSearchPage = 1;
    private int _totalSearchHits;
    private int _totalSearchPages = 1;
    private bool _hasSearched;
    private string _activeSearchQuery = string.Empty;
    private bool _isShowingSimilarContent;
    private bool _isShowingCompatibleAlternatives;
    private string _similarContentSourceTitle = string.Empty;
    private string _similarContentCriteria = string.Empty;
    private IReadOnlyList<string> _similarContentEnvironments = [];
    private PackOutputPlan? _lastOutputPlan;
    private PackOutputResult? _lastOutputResult;

    public PackBuilderViewModel(
        IPackContentCatalogService catalogService,
        IPackDependencyResolver dependencyResolver,
        IPackPlatformCatalogService platformCatalog,
        IPackPlatformVersionService platformVersionService,
        IJavaRuntimeService javaRuntimeService,
        ICurseForgeApiKeyService curseForgeApiKeyService,
        IPackDraftOutputService outputService,
        IMinecraftLauncherIntegrationService launcherIntegrationService,
        IModpackInstallLocationService installLocationService)
    {
        _catalogService = catalogService;
        _dependencyResolver = dependencyResolver;
        _platformCatalog = platformCatalog;
        _platformVersionService = platformVersionService
            ?? throw new ArgumentNullException(nameof(platformVersionService));
        _javaRuntimeService = javaRuntimeService
            ?? throw new ArgumentNullException(nameof(javaRuntimeService));
        _curseForgeApiKeyService = curseForgeApiKeyService
            ?? throw new ArgumentNullException(nameof(curseForgeApiKeyService));
        _outputService = outputService ?? throw new ArgumentNullException(nameof(outputService));
        _launcherIntegrationService = launcherIntegrationService
            ?? throw new ArgumentNullException(nameof(launcherIntegrationService));
        _installLocationService = installLocationService
            ?? throw new ArgumentNullException(nameof(installLocationService));

        ProviderFilters =
        [
            new("modrinth", "Modrinth", true),
            new("curseforge", "CurseForge", true)
        ];
        SortOptions =
        [
            new("relevance", "Best match"),
            new("downloads", "Most downloaded"),
            new("updated", "Recently updated"),
            new("newest", "Newest projects")
        ];
        PageSizeOptions =
        [
            new(20, "20 per source"),
            new(40, "40 per source"),
            new(50, "50 per source")
        ];
        _selectedSort = SortOptions[0];
        _selectedPageSize = PageSizeOptions[0];
        SearchPageNumbers.Add(1);

        foreach (var option in platformCatalog.GetBuildTargets())
        {
            BuildTargets.Add(option);
        }

        SearchCommand = new AsyncRelayCommand(SearchFromStartAsync, CanSearch);
        ShowMoreCommand = new AsyncRelayCommand(ShowMoreAsync, () => CanShowMore);
        PreviousSearchPageCommand = new AsyncRelayCommand(
            PreviousSearchPageAsync,
            () => CanGoToPreviousSearchPage);
        NextSearchPageCommand = new AsyncRelayCommand(
            NextSearchPageAsync,
            () => CanGoToNextSearchPage);
        FindSimilarCommand = new AsyncRelayCommand(FindSimilarAsync, () => CanFindSimilar);
        ClearDraftCommand = new AsyncRelayCommand(ClearDraftAsync, () => DraftItems.Count > 0 && !IsBusy);

        _selectedTarget = BuildTargets.First(option => option.Target == PackBuildTarget.ClientAndServer);
        RefreshPlatformOptions();
        RefreshContentKinds();
    }

    public ObservableCollection<PackBuildTargetOption> BuildTargets { get; } = [];

    public ObservableCollection<string> MinecraftVersions { get; } = [];

    public ObservableCollection<PackPlatformOption> ClientPlatforms { get; } = [];

    public ObservableCollection<PackPlatformOption> ServerPlatforms { get; } = [];

    public ObservableCollection<PackPlatformVersionOption> ClientRuntimeVersions { get; } = [];

    public ObservableCollection<PackPlatformVersionOption> ServerRuntimeVersions { get; } = [];

    public ObservableCollection<ServerContentKindOption> ContentKinds { get; } = [];

    public ObservableCollection<PackCategoryOption> Categories { get; } = [];

    public ObservableCollection<ModpackFilterOption> CategoryFilters { get; } = [];

    public ObservableCollection<ModpackFilterOption> ProviderFilters { get; }

    public ObservableCollection<PackCatalogSortOption> SortOptions { get; }

    public ObservableCollection<PackCatalogPageSizeOption> PageSizeOptions { get; }

    public ObservableCollection<int> SearchPageNumbers { get; } = [];

    public ObservableCollection<PackProviderStatus> ProviderStatuses { get; } = [];

    public ObservableCollection<ServerContentProject> SearchResults { get; } = [];

    public ObservableCollection<ServerContentVersion> Versions { get; } = [];

    public ObservableCollection<PackDraftItem> DraftItems { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand ShowMoreCommand { get; }

    public AsyncRelayCommand PreviousSearchPageCommand { get; }

    public AsyncRelayCommand NextSearchPageCommand { get; }

    public AsyncRelayCommand FindSimilarCommand { get; }

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
            OnPropertyChanged(nameof(ShowClientRuntimeVersion));
            OnPropertyChanged(nameof(ShowServerRuntimeVersion));
            OnPropertyChanged(nameof(OutputCapabilityTitle));
            OnPropertyChanged(nameof(OutputCapabilityMessage));
            RefreshContentKinds();
            EnsureContentKindSupported();
            ResetForSetupChange("Build target changed. Search again to create a compatible draft.");
            RefreshPlatformOptions();
        }
    }

    public string? SelectedMinecraftVersion
    {
        get => _selectedMinecraftVersion;
        set
        {
            if (SetProperty(ref _selectedMinecraftVersion, value))
            {
                RefreshPlatformOptions();
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
                OnPropertyChanged(nameof(ShowClientRuntimeVersion));
                if (_isRefreshingPlatformOptions)
                {
                    return;
                }

                _isRefreshingPlatformOptions = true;
                try
                {
                    RefreshServerPlatformOptions(_selectedServerPlatform?.Id);
                }
                finally
                {
                    _isRefreshingPlatformOptions = false;
                }
                EnsureContentKindSupported();
                ResetForSetupChange("Client platform changed. Search again to check every selected item.");
                QueuePlatformVersionRefresh();
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
                OnPropertyChanged(nameof(ShowServerRuntimeVersion));
                OnPropertyChanged(nameof(OutputCapabilityTitle));
                OnPropertyChanged(nameof(OutputCapabilityMessage));
                if (_isRefreshingPlatformOptions)
                {
                    return;
                }

                EnsureContentKindSupported();
                ResetForSetupChange("Server platform changed. Search again to check every selected item.");
                QueuePlatformVersionRefresh();
            }
        }
    }

    public PackPlatformVersionOption? SelectedClientRuntimeVersion
    {
        get => _selectedClientRuntimeVersion;
        set
        {
            if (SetProperty(ref _selectedClientRuntimeVersion, value))
            {
                AlignServerRuntimeVersion(value);
                PlatformVersionStatusText = CreatePlatformVersionStatusText();
                OnPropertyChanged(nameof(OutputCapabilityTitle));
                OnPropertyChanged(nameof(OutputCapabilityMessage));
                NotifyCommandStates();
            }
        }
    }

    public PackPlatformVersionOption? SelectedServerRuntimeVersion
    {
        get => _selectedServerRuntimeVersion;
        set
        {
            if (SetProperty(ref _selectedServerRuntimeVersion, value))
            {
                AlignClientRuntimeVersion(value);
                PlatformVersionStatusText = CreatePlatformVersionStatusText();
                OnPropertyChanged(nameof(OutputCapabilityTitle));
                OnPropertyChanged(nameof(OutputCapabilityMessage));
                NotifyCommandStates();
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

    public PackCatalogSortOption SelectedSort
    {
        get => _selectedSort;
        set => SetProperty(ref _selectedSort, value);
    }

    public PackCatalogPageSizeOption SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (value is not null && SetProperty(ref _selectedPageSize, value) && _hasSearched)
            {
                _ = SearchPageAsync(1, append: false);
            }
        }
    }

    public int CurrentSearchPage
    {
        get => _currentSearchPage;
        private set
        {
            if (SetProperty(ref _currentSearchPage, value))
            {
                NotifySearchPagingState();
            }
        }
    }

    public int TotalSearchHits
    {
        get => _totalSearchHits;
        private set
        {
            if (SetProperty(ref _totalSearchHits, value))
            {
                OnPropertyChanged(nameof(SearchPageSummary));
                OnPropertyChanged(nameof(CanShowMore));
            }
        }
    }

    public int TotalSearchPages
    {
        get => _totalSearchPages;
        private set
        {
            if (SetProperty(ref _totalSearchPages, value))
            {
                NotifySearchPagingState();
            }
        }
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
            OnPropertyChanged(nameof(CanFindSimilar));
            OnPropertyChanged(nameof(SimilarContentButtonText));
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

    public string PackName
    {
        get => _packName;
        set
        {
            if (SetProperty(ref _packName, value))
            {
                OnPropertyChanged(nameof(CanCreateOutput));
            }
        }
    }

    public string OutputStatusText
    {
        get => _outputStatusText;
        private set => SetProperty(ref _outputStatusText, value);
    }

    public string PlatformVersionStatusText
    {
        get => _platformVersionStatusText;
        private set => SetProperty(ref _platformVersionStatusText, value);
    }

    public string LastOutputDirectory
    {
        get => _lastOutputDirectory;
        private set
        {
            if (SetProperty(ref _lastOutputDirectory, value))
            {
                OnPropertyChanged(nameof(HasLastOutput));
            }
        }
    }

    public double OutputProgressPercent
    {
        get => _outputProgressPercent;
        private set => SetProperty(ref _outputProgressPercent, value);
    }

    public bool IsOutputProgressIndeterminate
    {
        get => _isOutputProgressIndeterminate;
        private set => SetProperty(ref _isOutputProgressIndeterminate, value);
    }

    public bool IsOutputBusy
    {
        get => _isOutputBusy;
        private set => SetProperty(ref _isOutputBusy, value);
    }

    public string LauncherStatusText
    {
        get => _launcherStatusText;
        private set => SetProperty(ref _launcherStatusText, value);
    }

    public bool IsLauncherBusy
    {
        get => _isLauncherBusy;
        private set
        {
            if (SetProperty(ref _isLauncherBusy, value))
            {
                OnPropertyChanged(nameof(CanRegisterLastClientOutput));
            }
        }
    }

    public bool HasLauncherProfile
    {
        get => _hasLauncherProfile;
        private set => SetProperty(ref _hasLauncherProfile, value);
    }

    public string CurseForgeConnectionStatusText
    {
        get => _curseForgeConnectionStatusText;
        private set => SetProperty(ref _curseForgeConnectionStatusText, value);
    }

    public bool IsCurseForgeConnectionBusy
    {
        get => _isCurseForgeConnectionBusy;
        private set
        {
            if (SetProperty(ref _isCurseForgeConnectionBusy, value))
            {
                OnPropertyChanged(nameof(IsCurseForgeConnectionNotBusy));
            }
        }
    }

    public bool IsCurseForgeConnectionNotBusy => !IsCurseForgeConnectionBusy;

    public bool IsCurseForgeApiKeyStored
    {
        get => _isCurseForgeApiKeyStored;
        private set => SetProperty(ref _isCurseForgeApiKeyStored, value);
    }

    public bool IsResolvingPlatformVersions
    {
        get => _isResolvingPlatformVersions;
        private set
        {
            if (SetProperty(ref _isResolvingPlatformVersions, value))
            {
                OnPropertyChanged(nameof(IsNotResolvingPlatformVersions));
                NotifyCommandStates();
            }
        }
    }

    public bool IsNotResolvingPlatformVersions => !IsResolvingPlatformVersions;

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

    public bool ShowClientRuntimeVersion => ShowClientPlatform
        && SelectedClientPlatform is { } platform
        && _platformVersionService.CanResolve(platform.Id);

    public bool ShowServerRuntimeVersion => ShowServerPlatform
        && SelectedServerPlatform is { } platform
        && _platformVersionService.CanResolve(platform.Id);

    public bool IsNotBusy => !IsBusy;

    public bool CanGoToPreviousSearchPage => !IsBusy && CurrentSearchPage > 1;

    public bool CanGoToNextSearchPage => !IsBusy && CurrentSearchPage < TotalSearchPages;

    public bool CanShowMore => CanGoToNextSearchPage && SearchResults.Count < TotalSearchHits;

    public bool CanFindSimilar => !IsBusy
        && (SelectedProject is { Categories.Count: > 0 }
            || (_hasSearched
                && !IsShowingSimilarContent
                && !string.IsNullOrWhiteSpace(_activeSearchQuery)));

    public bool IsShowingSimilarContent
    {
        get => _isShowingSimilarContent;
        private set
        {
            if (SetProperty(ref _isShowingSimilarContent, value))
            {
                OnPropertyChanged(nameof(SearchResultsTitle));
                OnPropertyChanged(nameof(SimilarContentExplanation));
            }
        }
    }

    public string SearchResultsTitle => IsShowingSimilarContent
        ? _isShowingCompatibleAlternatives
            ? $"Compatible alternatives to {_similarContentSourceTitle}"
            : $"Similar content to {_similarContentSourceTitle}"
        : "Compatible projects";

    public string SimilarContentExplanation => IsShowingSimilarContent
        ? $"These are inferred recommendations based on {_similarContentCriteria}; they are not title matches."
        : "Direct catalogue results for the current search and filters.";

    public string SimilarContentButtonText => SelectedProject is not null
        ? $"Find content similar to {SelectedProject.Title}"
        : _hasSearched && !string.IsNullOrWhiteSpace(_activeSearchQuery)
            ? $"Find compatible alternatives to “{_activeSearchQuery}”"
            : "Find similar content";

    public string SearchPageSummary => TotalSearchHits == 0
        ? "No matching catalogue entries"
        : $"{SearchResults.Count:N0} loaded  •  page {CurrentSearchPage:N0} of {TotalSearchPages:N0}  •  {TotalSearchHits:N0} across the responding sources";

    public bool HasSelectedProject => SelectedProject is not null;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool CanReviewAdd => SelectedProject is not null && SelectedVersion is not null && !IsBusy;

    public bool CanCreateOutput => (DraftItems.Count > 0 || CanBuildEmptyServerBaseline)
        && DraftItems.All(item => item.Placement != PackContentPlacement.Review)
        && !string.IsNullOrWhiteSpace(PackName)
        && !IsResolvingPlatformVersions
        && IsRuntimePairCompatible
        && (!ShowClientRuntimeVersion || SelectedClientRuntimeVersion is not null)
        && (!ShowServerRuntimeVersion || SelectedServerRuntimeVersion is not null)
        && !IsBusy;

    public bool IsRuntimePairCompatible
    {
        get
        {
            if (SelectedTarget?.Target != PackBuildTarget.ClientAndServer
                || SelectedClientRuntimeVersion is not { } client
                || SelectedServerRuntimeVersion is not { } server)
            {
                return true;
            }

            return client.LoaderId.Equals(server.LoaderId, StringComparison.OrdinalIgnoreCase)
                && client.Version.Equals(server.Version, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool CanBuildEmptyServerBaseline => SelectedTarget?.Target == PackBuildTarget.Server
        && SelectedServerRuntimeVersion?.CanPrepareServer == true;

    public bool HasLastOutput => !string.IsNullOrWhiteSpace(LastOutputDirectory);

    public bool HasLastClientOutput => (_lastOutputPlan?.Target is
            PackBuildTarget.Client or PackBuildTarget.ClientAndServer)
        && _lastOutputResult is not null;

    public bool CanRegisterLastClientOutput => HasLastClientOutput
        && !IsBusy
        && !IsLauncherBusy;

    public string ManagedOutputDirectory => _installLocationService.ManagedInstancesDirectory;

    public string ManagedOutputDirectoryText => $"Managed Instances folder: {ManagedOutputDirectory}";

    public string TargetGuidance => SelectedTarget?.Description ?? "Choose an output.";

    public string ClientPlatformGuidance => SelectedClientPlatform is null
        ? "Choose a client loader."
        : $"{SelectedClientPlatform.KindText}  •  {SelectedClientPlatform.CapabilityText}\n{SelectedClientPlatform.GuidanceText}{MinecraftCompatibilitySuffix}";

    public string ServerPlatformGuidance => SelectedServerPlatform is null
        ? "Choose a server platform."
        : $"{SelectedServerPlatform.KindText}  •  {SelectedServerPlatform.CapabilityText}\n{SelectedServerPlatform.GuidanceText}{MinecraftCompatibilitySuffix}";

    public string OutputCapabilityTitle => !IsRuntimePairCompatible
        ? "Match the linked loader versions"
        : (ShowClientRuntimeVersion, ShowServerRuntimeVersion) switch
        {
            (true, true) when SelectedServerRuntimeVersion?.CanPrepareServer == true =>
                "Runnable client + server output",
            (true, _) => "Minecraft Launcher client output",
            (_, true) when SelectedServerRuntimeVersion?.CanPrepareServer == true =>
                "Runnable server output",
            _ => "Content-only output"
        };

    public string OutputCapabilityMessage
    {
        get
        {
            if (!IsRuntimePairCompatible)
            {
                return "Choose the same loader family and exact loader version for both halves of this linked pack.";
            }

            var clientText = ShowClientRuntimeVersion && SelectedClientRuntimeVersion is { } clientVersion
                ? $"The isolated Client folder will use {SelectedClientPlatform?.DisplayName} {clientVersion.Version} and can be added directly to Minecraft Launcher."
                : ShowClientPlatform
                    ? "The selected client platform has no supported official-launcher installer yet."
                    : string.Empty;
            var serverText = ShowServerRuntimeVersion
                && SelectedServerRuntimeVersion is { CanPrepareServer: true } serverVersion
                    ? $" The manager will install {SelectedServerPlatform?.DisplayName} {serverVersion.Version} as a runnable server, select its new profile, and leave EULA acceptance for your explicit review. Java {GetRecommendedJavaText()} is recommended."
                    : ShowServerPlatform
                        ? " The server side will remain a verified content layout because no safe baseline installer is selected."
                        : string.Empty;
            return (clientText + serverText).Trim();
        }
    }

    public string SelectedVersionDetails => SelectedVersion is null
        ? "Choose a published version."
        : $"{SelectedVersion.CompatibilityText}\n{SelectedVersion.DependencyText}";

    public string DraftCountText => DraftItems.Count == 1
        ? "1 planned item"
        : $"{DraftItems.Count:N0} planned items";

    public void EnsureLoaded()
    {
        if (!_isCurseForgeConnectionLoaded && !IsCurseForgeConnectionBusy)
        {
            _ = RefreshCurseForgeConnectionAsync();
        }

        if (!_isLoaded)
        {
            _ = LoadMinecraftVersionsAsync();
        }
    }

    public async Task<bool> ConnectCurseForgeAsync(string apiKey)
    {
        if (IsCurseForgeConnectionBusy)
        {
            return false;
        }

        IsCurseForgeConnectionBusy = true;
        CurseForgeConnectionStatusText = "Validating the approved key directly with CurseForge…";
        try
        {
            var status = await _curseForgeApiKeyService.ValidateAndStoreAsync(apiKey);
            ApplyCurseForgeStatus(status);
            if (status.IsValid)
            {
                ProviderStatuses.Clear();
                StatusText = "CurseForge is connected. Search again to include it with every other available provider.";
            }

            return status.IsValid;
        }
        catch (Exception exception) when (
            exception is ArgumentException or HttpRequestException or InvalidOperationException
            or IOException or TaskCanceledException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            CurseForgeConnectionStatusText = $"The key could not be connected: {exception.Message}";
            return false;
        }
        finally
        {
            IsCurseForgeConnectionBusy = false;
            _isCurseForgeConnectionLoaded = true;
        }
    }

    public async Task<PackOutputPlan?> PrepareManagedOutputAsync()
    {
        try
        {
            return await PrepareOutputAsync(_installLocationService.EnsureManagedInstancesDirectory());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OutputStatusText = $"The managed Instances folder could not be prepared: {exception.Message}";
            return null;
        }
    }

    public async Task<PackOutputPlan?> PrepareOutputAsync(string destinationParentDirectory)
    {
        if (!CanCreateOutput || SelectedTarget is null)
        {
            OutputStatusText = DraftItems.Count == 0 && !CanBuildEmptyServerBaseline
                ? "Add at least one compatible item before creating an output."
                : "Resolve every placement review and enter a valid pack name first.";
            return null;
        }

        BeginBusy();
        OutputStatusText = "Refreshing provider metadata and validating the output plan…";
        try
        {
            var plan = await _outputService.CreatePlanAsync(new PackOutputRequest(
                PackName,
                SelectedTarget.Target,
                SelectedMinecraftVersion ?? string.Empty,
                ShowClientPlatform ? SelectedClientPlatform?.Id ?? string.Empty : string.Empty,
                ShowServerPlatform ? SelectedServerPlatform?.Id ?? string.Empty : string.Empty,
                ShowClientRuntimeVersion ? SelectedClientRuntimeVersion?.LoaderId ?? string.Empty : string.Empty,
                ShowClientRuntimeVersion ? SelectedClientRuntimeVersion?.Version ?? string.Empty : string.Empty,
                ShowServerRuntimeVersion ? SelectedServerRuntimeVersion?.LoaderId ?? string.Empty : string.Empty,
                ShowServerRuntimeVersion ? SelectedServerRuntimeVersion?.Version ?? string.Empty : string.Empty,
                destinationParentDirectory,
                DraftItems.ToArray()));
            OutputStatusText = $"{plan.SummaryText} Review the setup summary before continuing.";
            return plan;
        }
        catch (Exception exception) when (
            exception is ArgumentException or HttpRequestException or InvalidDataException
            or InvalidOperationException or IOException or JsonException or TaskCanceledException
            or UnauthorizedAccessException)
        {
            OutputStatusText = $"The output plan could not be created: {exception.Message}";
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task<PackOutputResult?> CreateOutputAsync(PackOutputPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (IsBusy)
        {
            return null;
        }

        BeginBusy();
        IsOutputBusy = true;
        OutputProgressPercent = 0;
        IsOutputProgressIndeterminate = true;
        LastOutputDirectory = string.Empty;
        _lastOutputPlan = null;
        _lastOutputResult = null;
        HasLauncherProfile = false;
        OnPropertyChanged(nameof(HasLastClientOutput));
        OnPropertyChanged(nameof(CanRegisterLastClientOutput));
        try
        {
            var result = await _outputService.CreateOutputAsync(
                plan,
                new Progress<PackOutputProgress>(UpdateOutputProgress));
            LastOutputDirectory = result.OutputDirectory;
            _lastOutputPlan = plan;
            _lastOutputResult = result;
            OnPropertyChanged(nameof(HasLastClientOutput));
            OnPropertyChanged(nameof(CanRegisterLastClientOutput));
            if (HasLastClientOutput)
            {
                LauncherStatusText =
                    "Client files are ready. Close Minecraft Launcher, then add this isolated game directory to it.";
            }
            OutputStatusText = result.ServerBaselinePrepared
                ? $"Created {result.DownloadedFileCount:N0} verified download{(result.DownloadedFileCount == 1 ? string.Empty : "s")}, "
                    + $"arranged {result.ArrangedFileCount:N0} file cop{(result.ArrangedFileCount == 1 ? "y" : "ies")}, "
                    + $"and prepared the runnable server baseline with {result.ServerLauncherFileName}. The profile and EULA review are opening now."
                : $"Created {result.DownloadedFileCount:N0} verified download{(result.DownloadedFileCount == 1 ? string.Empty : "s")} "
                    + $"and arranged {result.ArrangedFileCount:N0} client/server file cop{(result.ArrangedFileCount == 1 ? "y" : "ies")}. "
                    + "This output remains content-only for the selected platform.";
            return result;
        }
        catch (Exception exception) when (
            exception is ArgumentException or HttpRequestException or InvalidDataException
            or InvalidOperationException or IOException or JsonException or TaskCanceledException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            OutputStatusText = $"The verified content bundle could not be created: {exception.Message}";
            return null;
        }
        finally
        {
            IsOutputBusy = false;
            EndBusy();
        }
    }

    public async Task<MinecraftLauncherInstallResult?> RegisterLastClientWithLauncherAsync()
    {
        if (!CanRegisterLastClientOutput
            || _lastOutputPlan is not { } plan
            || _lastOutputResult is not { } result)
        {
            LauncherStatusText = "Build a client output before adding it to Minecraft Launcher.";
            return null;
        }

        IsLauncherBusy = true;
        LauncherStatusText = "Preparing Minecraft Launcher integration…";
        try
        {
            var clientDirectory = Path.Combine(result.OutputDirectory, "Client");
            var installed = await _launcherIntegrationService.InstallAsync(
                new MinecraftLauncherInstallRequest(
                    plan.PackName,
                    plan.MinecraftVersion,
                    plan.ClientLoaderId,
                    plan.ClientLoaderVersion,
                    clientDirectory,
                    result.ManifestPath),
                new Progress<string>(message => LauncherStatusText = message));
            HasLauncherProfile = true;
            LauncherStatusText = installed.Message;
            return installed;
        }
        catch (Exception exception) when (
            exception is ArgumentException or HttpRequestException or InvalidDataException
                or InvalidOperationException or IOException or JsonException
                or TaskCanceledException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            HasLauncherProfile = false;
            LauncherStatusText = $"The client files are safe, but Minecraft Launcher setup did not finish: {exception.Message}";
            return null;
        }
        finally
        {
            IsLauncherBusy = false;
        }
    }

    public bool TryOpenMinecraftLauncher()
    {
        var opened = _launcherIntegrationService.TryOpenLauncher(out var message);
        LauncherStatusText = message;
        return opened;
    }

    public bool RemoveCurseForgeConnection()
    {
        if (IsCurseForgeConnectionBusy)
        {
            return false;
        }

        try
        {
            _curseForgeApiKeyService.Remove();
            ApplyCurseForgeStatus(new CurseForgeApiKeyStatus(
                false,
                false,
                "CurseForge was disconnected and its key was removed from Windows Credential Manager."));
            ClearSearchSelection();
            ProviderStatuses.Clear();
            StatusText = "CurseForge was disconnected. Modrinth remains available without an account.";
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException or IOException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            CurseForgeConnectionStatusText = $"The stored key could not be removed: {exception.Message}";
            return false;
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

    public async Task<PackResolutionPlan?> PrepareOptionalDependenciesAsync(
        PackResolutionPlan basePlan,
        IReadOnlyList<PackOptionalDependencyChoice> selectedChoices)
    {
        ArgumentNullException.ThrowIfNull(basePlan);
        ArgumentNullException.ThrowIfNull(selectedChoices);
        if (basePlan.Conflicts.Count > 0 || selectedChoices.Count == 0 || SelectedTarget is null)
        {
            return basePlan;
        }

        BeginBusy();
        DraftStatusText = "Resolving the chosen optional dependencies and everything they require…";
        try
        {
            var items = basePlan.Items.ToList();
            var warnings = basePlan.Warnings.ToList();
            var conflicts = basePlan.Conflicts.ToList();
            var nestedOptional = new List<PackOptionalDependencyChoice>();
            foreach (var choice in selectedChoices
                         .DistinctBy(
                             choice => $"{choice.ProviderId}:{choice.ProjectId}",
                             StringComparer.OrdinalIgnoreCase))
            {
                var existingItems = DraftItems.Concat(items).ToArray();
                var project = CreateDependencyProject(choice.Kind, choice.Version, choice.DisplayName);
                var optionalPlan = await _dependencyResolver.ResolveAsync(
                    new PackResolveRequest(
                        SelectedTarget.Target,
                        SelectedMinecraftVersion ?? string.Empty,
                        GetClientLoaderIds(),
                        GetServerLoaderIds(choice.Kind),
                        project,
                        choice.Version,
                        existingItems)
                    {
                        RootIsDependency = true,
                        RootDependencyType = "optional",
                        RootReason = $"Optional dependency selected for {choice.OwnerName}"
                    });
                foreach (var item in optionalPlan.Items)
                {
                    if (!items.Any(existing =>
                            existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                            && existing.VersionId.Equals(item.VersionId, StringComparison.OrdinalIgnoreCase)))
                    {
                        items.Add(item);
                    }
                }

                warnings.AddRange(optionalPlan.Warnings);
                conflicts.AddRange(optionalPlan.Conflicts);
                nestedOptional.AddRange(optionalPlan.OptionalDependencies);
            }

            if (nestedOptional.Count > 0)
            {
                warnings.Add(
                    $"The chosen optional content declares {nestedOptional.Count:N0} further optional dependenc{(nestedOptional.Count == 1 ? "y" : "ies")}; those were left out and can be added separately.");
            }

            var combined = new PackResolutionPlan(
                items,
                warnings.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray(),
                conflicts.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray());
            DraftStatusText = combined.SummaryText;
            return combined;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
                or InvalidOperationException or JsonException or TaskCanceledException)
        {
            DraftStatusText = $"The optional dependencies could not be resolved: {exception.Message}";
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task<bool> RemoveDraftItemAsync(PackDraftItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanRemove || IsBusy)
        {
            DraftStatusText = item.CanRemove
                ? "Wait for the current builder operation to finish."
                : $"{item.DisplayName} is required by another draft item. Remove the item that requires it instead.";
            return false;
        }

        var explicitSelections = DraftItems
            .Where(existing => existing.IsExplicitSelection
                && !(existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                    && existing.VersionId.Equals(item.VersionId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (explicitSelections.Length == 0)
        {
            DraftItems.Clear();
            RefreshDraftState($"Removed {item.DisplayName}. The draft is now empty.");
            return true;
        }

        BeginBusy();
        DraftStatusText = $"Removing {item.DisplayName} and rebuilding only the dependencies still required…";
        try
        {
            var rebuilt = new List<PackDraftItem>();
            foreach (var selection in explicitSelections)
            {
                var version = await _catalogService.GetVersionAsync(
                    selection.ProviderId,
                    selection.VersionId);
                var project = CreateDependencyProject(selection.Kind, version, selection.DisplayName);
                var plan = await _dependencyResolver.ResolveAsync(
                    new PackResolveRequest(
                        SelectedTarget?.Target ?? PackBuildTarget.ClientAndServer,
                        SelectedMinecraftVersion ?? string.Empty,
                        GetClientLoaderIds(),
                        GetServerLoaderIds(selection.Kind),
                        project,
                        version,
                        rebuilt.ToArray())
                    {
                        RootIsDependency = selection.IsDependency,
                        RootDependencyType = selection.DependencyType,
                        RootReason = selection.Reason
                    });
                if (!plan.IsReady)
                {
                    DraftStatusText = $"{item.DisplayName} was not removed because the remaining draft could not be rebuilt safely: {plan.SummaryText}";
                    return false;
                }

                foreach (var rebuiltItem in plan.Items)
                {
                    if (!rebuilt.Any(existing =>
                            existing.ProviderId.Equals(rebuiltItem.ProviderId, StringComparison.OrdinalIgnoreCase)
                            && existing.VersionId.Equals(rebuiltItem.VersionId, StringComparison.OrdinalIgnoreCase)))
                    {
                        rebuilt.Add(rebuiltItem);
                    }
                }
            }

            DraftItems.Clear();
            foreach (var rebuiltItem in rebuilt)
            {
                DraftItems.Add(rebuiltItem);
            }

            RefreshDraftState(
                $"Removed {item.DisplayName}. Dependencies that are still needed by the remaining selections were kept.");
            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
                or InvalidOperationException or JsonException or TaskCanceledException)
        {
            DraftStatusText = $"{item.DisplayName} was not removed because the remaining draft could not be validated: {exception.Message}";
            return false;
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

        var addedItems = new List<PackDraftItem>();
        foreach (var item in plan.Items)
        {
            if (!DraftItems.Any(existing =>
                existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                && existing.VersionId.Equals(item.VersionId, StringComparison.OrdinalIgnoreCase)))
            {
                DraftItems.Add(item);
                addedItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(DraftCountText));
        var requiredCount = addedItems.Count(item => item.DependencyType == "required");
        var optionalCount = addedItems.Count(item => item.DependencyType == "optional");
        var additionText = requiredCount == 0
            ? $"Added {addedItems.Count:N0} selected item{(addedItems.Count == 1 ? string.Empty : "s")}."
            : $"Added the selected item and {requiredCount:N0} required dependenc{(requiredCount == 1 ? "y" : "ies")} automatically.";
        if (optionalCount > 0)
        {
            additionText += $" Included {optionalCount:N0} chosen optional dependenc{(optionalCount == 1 ? "y" : "ies")}.";
        }
        var noticeText = plan.Warnings.Count == 0
            ? string.Empty
            : $" Optional/review notice: {string.Join(" ", plan.Warnings.Take(2))}"
                + (plan.Warnings.Count > 2 ? $" (+{plan.Warnings.Count - 2:N0} more)" : string.Empty);
        DraftStatusText = $"{DraftCountText}. {additionText}{noticeText} Planning draft only — nothing has been downloaded or installed.";
        ClearDraftCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
    }

    private static ServerContentProject CreateDependencyProject(
        ServerContentKind kind,
        ServerContentVersion version,
        string displayName) => new(
            version.ProviderId,
            version.ProjectId,
            version.ProjectId,
            displayName,
            string.Empty,
            version.ProviderId,
            string.Empty,
            0,
            kind,
            version.MinecraftVersions,
            [],
            [version.Environment]);

    private void RefreshDraftState(string message)
    {
        OnPropertyChanged(nameof(DraftCountText));
        DraftStatusText = $"{message} Planning draft only — no files have been changed.";
        ClearDraftCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
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
            RefreshPlatformOptions();
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

    private async Task RefreshCurseForgeConnectionAsync()
    {
        IsCurseForgeConnectionBusy = true;
        CurseForgeConnectionStatusText = "Checking locally stored CurseForge developer access…";
        try
        {
            ApplyCurseForgeStatus(await _curseForgeApiKeyService.ValidateStoredAsync());
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidOperationException or IOException
            or TaskCanceledException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            try
            {
                IsCurseForgeApiKeyStored = !string.IsNullOrWhiteSpace(_curseForgeApiKeyService.GetApiKey());
            }
            catch
            {
                IsCurseForgeApiKeyStored = false;
            }

            CurseForgeConnectionStatusText = IsCurseForgeApiKeyStored
                ? $"A key is stored securely, but it could not be verified right now: {exception.Message}"
                : $"CurseForge developer access could not be checked: {exception.Message}";
        }
        finally
        {
            IsCurseForgeConnectionBusy = false;
            _isCurseForgeConnectionLoaded = true;
        }
    }

    private void ApplyCurseForgeStatus(CurseForgeApiKeyStatus status)
    {
        IsCurseForgeApiKeyStored = status.HasStoredKey;
        CurseForgeConnectionStatusText = status.Message;
    }

    public Task SearchFromStartAsync()
    {
        ClearSimilarSearchContext();
        _activeSearchQuery = SearchText.Trim();
        OnPropertyChanged(nameof(CanFindSimilar));
        OnPropertyChanged(nameof(SimilarContentButtonText));
        return SearchPageAsync(1, append: false);
    }

    public Task GoToSearchPageAsync(int pageNumber) =>
        pageNumber < 1 || pageNumber > TotalSearchPages || pageNumber == CurrentSearchPage
            ? Task.CompletedTask
            : SearchPageAsync(pageNumber, append: false);

    private Task PreviousSearchPageAsync() => CanGoToPreviousSearchPage
        ? SearchPageAsync(CurrentSearchPage - 1, append: false)
        : Task.CompletedTask;

    private Task NextSearchPageAsync() => CanGoToNextSearchPage
        ? SearchPageAsync(CurrentSearchPage + 1, append: false)
        : Task.CompletedTask;

    private Task ShowMoreAsync() => CanShowMore
        ? SearchPageAsync(CurrentSearchPage + 1, append: true)
        : Task.CompletedTask;

    public async Task FindSimilarAsync()
    {
        var source = SelectedProject;
        var isAlternativeSearch = false;
        var matchingCategory = FindDefiningCategory(source);
        if (matchingCategory is null && !string.IsNullOrWhiteSpace(_activeSearchQuery))
        {
            source = await FindAlternativeReferenceAsync(_activeSearchQuery);
            isAlternativeSearch = source is not null;
            matchingCategory = FindDefiningCategory(source);
        }

        if (source is null || matchingCategory is null)
        {
            StatusText =
                string.IsNullOrWhiteSpace(_activeSearchQuery)
                    ? "Select a project that publishes a recognised content category before finding similar content."
                    : $"No published category could be identified for {_activeSearchQuery}, so compatible alternatives cannot be inferred reliably.";
            return;
        }

        foreach (var filter in CategoryFilters)
        {
            filter.IsSelected = ReferenceEquals(filter, matchingCategory);
        }

        _selectedCategory = Categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
        _isShowingCompatibleAlternatives = isAlternativeSearch;
        _similarContentSourceTitle = isAlternativeSearch ? _activeSearchQuery : source.Title;
        _similarContentEnvironments = GetSimilarityEnvironments(source.Environments);
        _similarContentCriteria = isAlternativeSearch
            ? $"the {matchingCategory.DisplayName} category and client/server compatibility published for {source.Title}, Minecraft {SelectedMinecraftVersion}, and the selected loader"
            : $"the {matchingCategory.DisplayName} category, client/server compatibility, Minecraft {SelectedMinecraftVersion}, and the selected loader";
        IsShowingSimilarContent = true;
        OnPropertyChanged(nameof(SearchResultsTitle));
        OnPropertyChanged(nameof(SimilarContentExplanation));
        await SearchPageAsync(1, append: false);
    }

    private async Task<ServerContentProject?> FindAlternativeReferenceAsync(string query)
    {
        if (SelectedTarget is null || SelectedContentKind?.Kind is not { } kind)
        {
            return null;
        }

        var selectedProviderIds = ProviderFilters
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        if (selectedProviderIds.Length == 0)
        {
            StatusText = "Choose at least one content provider before finding alternatives.";
            return null;
        }

        BeginBusy();
        StatusText = $"Checking every Minecraft version for {query} so its content type can be identified…";
        try
        {
            var page = await _catalogService.SearchAsync(new PackCatalogSearchRequest(
                query,
                string.Empty,
                kind,
                SelectedTarget.Target,
                [],
                [],
                0,
                Math.Min(20, SelectedPageSize.Value))
            {
                ProviderIds = selectedProviderIds,
                Sort = "relevance"
            });
            return page.Items
                .Where(project => project.Categories.Any(category =>
                    CategoryFilters.Any(filter => CategoriesMatch(category, filter))))
                .OrderByDescending(project => GetAlternativeReferenceScore(project, query))
                .ThenByDescending(project => project.Downloads)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
                or InvalidOperationException or JsonException or TaskCanceledException)
        {
            StatusText = $"Compatible alternatives could not be prepared: {exception.Message}";
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task SearchPageAsync(int pageNumber, bool append)
    {
        if (!CanSearch() || SelectedTarget is null || SelectedContentKind?.Kind is not { } kind)
        {
            return;
        }

        var selectedProviderIds = ProviderFilters
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        if (selectedProviderIds.Length == 0)
        {
            StatusText = "Choose at least one content provider to search.";
            return;
        }

        BeginBusy();
        if (!append)
        {
            ClearSearchSelection(preserveSearchContext: true);
        }

        ProviderStatuses.Clear();
        var pageSize = SelectedPageSize.Value;
        var offset = Math.Max(0, (pageNumber - 1) * pageSize);
        var categories = CategoryFilters
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        if (categories.Length == 0 && !string.IsNullOrWhiteSpace(SelectedCategory?.Id))
        {
            categories = [SelectedCategory.Id];
        }

        StatusText = IsShowingSimilarContent
            ? $"Finding content related to {_similarContentSourceTitle} across the selected providers…"
            : "Searching the selected providers independently…";
        try
        {
            var request = new PackCatalogSearchRequest(
                IsShowingSimilarContent ? string.Empty : _activeSearchQuery,
                SelectedMinecraftVersion ?? string.Empty,
                kind,
                SelectedTarget.Target,
                GetSearchLoaderIds(kind),
                categories,
                offset,
                pageSize)
            {
                ProviderIds = selectedProviderIds,
                Environments = IsShowingSimilarContent ? _similarContentEnvironments : [],
                Sort = SelectedSort.Id
            };
            var page = await _catalogService.SearchAsync(request);
            foreach (var provider in page.Providers)
            {
                ProviderStatuses.Add(provider);
            }

            foreach (var item in page.Items)
            {
                if (!SearchResults.Any(existing =>
                    existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                    && existing.ProjectId.Equals(item.ProjectId, StringComparison.OrdinalIgnoreCase)))
                {
                    SearchResults.Add(item);
                }
            }

            _hasSearched = true;
            OnPropertyChanged(nameof(CanFindSimilar));
            OnPropertyChanged(nameof(SimilarContentButtonText));
            CurrentSearchPage = pageNumber;
            TotalSearchHits = page.TotalHits;
            TotalSearchPages = page.MaximumProviderHits == 0
                ? 1
                : (int)Math.Ceiling(page.MaximumProviderHits / (double)pageSize);
            RefreshSearchPageNumbers();
            StatusText = SearchResults.Count == 0
                ? $"No compatible results. {page.ProviderSummary}. Try broader content filters or another platform."
                : IsShowingSimilarContent
                    ? $"{SearchResults.Count:N0} related result{(SearchResults.Count == 1 ? string.Empty : "s")} loaded  •  {page.ProviderSummary}. Similarity is inferred from published categories and compatibility."
                    : $"{SearchResults.Count:N0} compatible result{(SearchResults.Count == 1 ? string.Empty : "s")} loaded  •  {page.ProviderSummary}.";
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
        CategoryFilters.Clear();
        var kind = SelectedContentKind?.Kind ?? ServerContentKind.Mod;
        foreach (var category in _platformCatalog.GetCategories(kind))
        {
            Categories.Add(category);
            if (!string.IsNullOrWhiteSpace(category.Id))
            {
                CategoryFilters.Add(new ModpackFilterOption(category.Id, category.DisplayName));
            }
        }

        _selectedCategory = Categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private string MinecraftCompatibilitySuffix => string.IsNullOrWhiteSpace(SelectedMinecraftVersion)
        ? string.Empty
        : $"\nAvailable for Minecraft {SelectedMinecraftVersion}.";

    private void RefreshPlatformOptions()
    {
        var preferredClientId = _selectedClientPlatform?.Id;
        var preferredServerId = _selectedServerPlatform?.Id;
        _isRefreshingPlatformOptions = true;
        try
        {
            ClientPlatforms.Clear();
            foreach (var option in _platformCatalog.GetClientPlatforms(SelectedMinecraftVersion))
            {
                ClientPlatforms.Add(option);
            }

            _selectedClientPlatform = ClientPlatforms.FirstOrDefault(option =>
                    option.Id.Equals(preferredClientId, StringComparison.OrdinalIgnoreCase))
                ?? SelectPreferredClientPlatform();
            RefreshServerPlatformOptions(preferredServerId);

            OnPropertyChanged(nameof(SelectedClientPlatform));
        }
        finally
        {
            _isRefreshingPlatformOptions = false;
        }

        OnPropertyChanged(nameof(ClientPlatformGuidance));
        OnPropertyChanged(nameof(ServerPlatformGuidance));
        OnPropertyChanged(nameof(ShowClientRuntimeVersion));
        OnPropertyChanged(nameof(ShowServerRuntimeVersion));
        OnPropertyChanged(nameof(OutputCapabilityTitle));
        OnPropertyChanged(nameof(OutputCapabilityMessage));
        EnsureContentKindSupported();
        NotifyCommandStates();
        QueuePlatformVersionRefresh();
    }

    private void QueuePlatformVersionRefresh() => _ = RefreshPlatformVersionsAsync();

    private async Task RefreshPlatformVersionsAsync()
    {
        var requestId = Interlocked.Increment(ref _platformVersionRequestId);
        var minecraftVersion = SelectedMinecraftVersion;
        var clientPlatform = ShowClientPlatform ? SelectedClientPlatform : null;
        var serverPlatform = ShowServerPlatform ? SelectedServerPlatform : null;
        var previousClientPlatformId = SelectedClientRuntimeVersion?.PlatformId;
        var previousClientVersion = SelectedClientRuntimeVersion?.Version;
        var previousServerPlatformId = SelectedServerRuntimeVersion?.PlatformId;
        var previousServerVersion = SelectedServerRuntimeVersion?.Version;

        ClientRuntimeVersions.Clear();
        ServerRuntimeVersions.Clear();
        _selectedClientRuntimeVersion = null;
        _selectedServerRuntimeVersion = null;
        OnPropertyChanged(nameof(SelectedClientRuntimeVersion));
        OnPropertyChanged(nameof(SelectedServerRuntimeVersion));
        NotifyCommandStates();

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            PlatformVersionStatusText =
                "Choose a Minecraft version and platform to resolve exact official loader versions.";
            return;
        }

        var clientCanResolve = clientPlatform is not null
            && _platformVersionService.CanResolve(clientPlatform.Id);
        var serverCanResolve = serverPlatform is not null
            && _platformVersionService.CanResolve(serverPlatform.Id);
        if (!clientCanResolve && !serverCanResolve)
        {
            PlatformVersionStatusText = CreatePlatformVersionStatusText();
            OnPropertyChanged(nameof(OutputCapabilityTitle));
            OnPropertyChanged(nameof(OutputCapabilityMessage));
            return;
        }

        IsResolvingPlatformVersions = true;
        PlatformVersionStatusText = "Resolving exact loader versions from official catalogues…";
        try
        {
            var clientTask = clientCanResolve
                ? _platformVersionService.GetVersionsAsync(clientPlatform!.Id, minecraftVersion)
                : Task.FromResult<IReadOnlyList<PackPlatformVersionOption>>([]);
            var serverTask = serverCanResolve
                ? _platformVersionService.GetVersionsAsync(serverPlatform!.Id, minecraftVersion)
                : Task.FromResult<IReadOnlyList<PackPlatformVersionOption>>([]);
            await Task.WhenAll(clientTask, serverTask);
            if (requestId != _platformVersionRequestId)
            {
                return;
            }

            foreach (var option in await clientTask)
            {
                ClientRuntimeVersions.Add(option);
            }

            foreach (var option in await serverTask)
            {
                ServerRuntimeVersions.Add(option);
            }

            _selectedClientRuntimeVersion = ClientRuntimeVersions.FirstOrDefault(option =>
                    option.PlatformId.Equals(previousClientPlatformId, StringComparison.OrdinalIgnoreCase)
                    && option.Version.Equals(previousClientVersion, StringComparison.OrdinalIgnoreCase))
                ?? ClientRuntimeVersions.FirstOrDefault();
            _selectedServerRuntimeVersion = ServerRuntimeVersions.FirstOrDefault(option =>
                    option.PlatformId.Equals(previousServerPlatformId, StringComparison.OrdinalIgnoreCase)
                    && option.Version.Equals(previousServerVersion, StringComparison.OrdinalIgnoreCase))
                ?? ServerRuntimeVersions.FirstOrDefault();
            AlignServerRuntimeVersion(_selectedClientRuntimeVersion);
            OnPropertyChanged(nameof(SelectedClientRuntimeVersion));
            OnPropertyChanged(nameof(SelectedServerRuntimeVersion));
            PlatformVersionStatusText = CreatePlatformVersionStatusText();
            OnPropertyChanged(nameof(OutputCapabilityTitle));
            OnPropertyChanged(nameof(OutputCapabilityMessage));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
            or NotSupportedException or JsonException or TaskCanceledException)
        {
            if (requestId == _platformVersionRequestId)
            {
                PlatformVersionStatusText =
                    $"Exact loader versions could not be resolved: {exception.Message}";
            }
        }
        finally
        {
            if (requestId == _platformVersionRequestId)
            {
                IsResolvingPlatformVersions = false;
                NotifyCommandStates();
            }
        }
    }

    private string CreatePlatformVersionStatusText()
    {
        var details = new List<string>(2);
        if (ShowClientPlatform && SelectedClientPlatform is { } clientPlatform)
        {
            details.Add(SelectedClientRuntimeVersion is { } clientVersion
                ? $"Client: {clientPlatform.DisplayName} {clientVersion.Version} ({clientVersion.StabilityText.ToLowerInvariant()})"
                : $"Client: {clientPlatform.DisplayName} remains content-only");
        }

        if (ShowServerPlatform && SelectedServerPlatform is { } serverPlatform)
        {
            details.Add(SelectedServerRuntimeVersion is { } serverVersion
                ? $"Server: {serverPlatform.DisplayName} {serverVersion.Version} ({serverVersion.StabilityText.ToLowerInvariant()}) • Java {GetRecommendedJavaText()}"
                : $"Server: {serverPlatform.DisplayName} remains content-only");
        }

        return details.Count == 0
            ? "Choose a target platform."
            : string.Join("  •  ", details);
    }

    private string GetRecommendedJavaText() =>
        _javaRuntimeService.GetRecommendedJavaMajor(SelectedMinecraftVersion ?? string.Empty)?.ToString()
        ?? "not determined";

    private PackPlatformOption? SelectPreferredClientPlatform()
    {
        string[] preferredIds =
        [
            "fabric-client",
            "neoforge-client",
            "forge-client",
            "quilt-client",
            "vanilla-client"
        ];
        return preferredIds
            .Select(id => ClientPlatforms.FirstOrDefault(option => option.Id == id))
            .FirstOrDefault(option => option is not null)
            ?? ClientPlatforms.FirstOrDefault();
    }

    private void RefreshServerPlatformOptions(string? preferredServerId)
    {
        var available = _platformCatalog
            .GetServerPlatforms(SelectedMinecraftVersion)
            .Where(option => IsServerPlatformCompatibleWithClient(option, _selectedClientPlatform))
            .ToArray();
        ServerPlatforms.Clear();
        foreach (var option in available)
        {
            ServerPlatforms.Add(option);
        }

        _selectedServerPlatform = ServerPlatforms.FirstOrDefault(option =>
                option.Id.Equals(preferredServerId, StringComparison.OrdinalIgnoreCase))
            ?? SelectPreferredServerPlatform(_selectedClientPlatform);
        OnPropertyChanged(nameof(SelectedServerPlatform));
        OnPropertyChanged(nameof(ServerPlatformGuidance));
        OnPropertyChanged(nameof(ShowServerRuntimeVersion));
        OnPropertyChanged(nameof(OutputCapabilityTitle));
        OnPropertyChanged(nameof(OutputCapabilityMessage));
    }

    private bool IsServerPlatformCompatibleWithClient(
        PackPlatformOption serverPlatform,
        PackPlatformOption? clientPlatform)
    {
        if (SelectedTarget?.Target != PackBuildTarget.ClientAndServer || clientPlatform is null)
        {
            return true;
        }

        return clientPlatform.Id switch
        {
            "vanilla-client" => serverPlatform.Id is "vanilla-server" or "paper-server",
            "fabric-client" => serverPlatform.Id == "fabric-server",
            "quilt-client" => serverPlatform.Id == "quilt-server",
            "forge-client" => serverPlatform.Id is "forge-server" or "hybrid-forge-server",
            "neoforge-client" => serverPlatform.Id == "neoforge-server",
            _ => false
        };
    }

    private void AlignServerRuntimeVersion(PackPlatformVersionOption? clientVersion)
    {
        if (SelectedTarget?.Target != PackBuildTarget.ClientAndServer || clientVersion is null)
        {
            return;
        }

        var matching = ServerRuntimeVersions.FirstOrDefault(option =>
            option.LoaderId.Equals(clientVersion.LoaderId, StringComparison.OrdinalIgnoreCase)
            && option.Version.Equals(clientVersion.Version, StringComparison.OrdinalIgnoreCase));
        if (matching is not null && !Equals(_selectedServerRuntimeVersion, matching))
        {
            _selectedServerRuntimeVersion = matching;
            OnPropertyChanged(nameof(SelectedServerRuntimeVersion));
        }
    }

    private void AlignClientRuntimeVersion(PackPlatformVersionOption? serverVersion)
    {
        if (SelectedTarget?.Target != PackBuildTarget.ClientAndServer || serverVersion is null)
        {
            return;
        }

        var matching = ClientRuntimeVersions.FirstOrDefault(option =>
            option.LoaderId.Equals(serverVersion.LoaderId, StringComparison.OrdinalIgnoreCase)
            && option.Version.Equals(serverVersion.Version, StringComparison.OrdinalIgnoreCase));
        if (matching is not null && !Equals(_selectedClientRuntimeVersion, matching))
        {
            _selectedClientRuntimeVersion = matching;
            OnPropertyChanged(nameof(SelectedClientRuntimeVersion));
        }
    }

    private PackPlatformOption? SelectPreferredServerPlatform(PackPlatformOption? clientPlatform)
    {
        var matchingServerId = clientPlatform?.Id switch
        {
            "vanilla-client" => "vanilla-server",
            "fabric-client" => "fabric-server",
            "quilt-client" => "quilt-server",
            "forge-client" => "forge-server",
            "neoforge-client" => "neoforge-server",
            _ => string.Empty
        };
        return ServerPlatforms.FirstOrDefault(option => option.Id == matchingServerId)
            ?? ServerPlatforms.FirstOrDefault(option => option.SupportsMods)
            ?? ServerPlatforms.FirstOrDefault();
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

    private void ClearSearchSelection(bool preserveSearchContext = false)
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
        OnPropertyChanged(nameof(CanFindSimilar));
        OnPropertyChanged(nameof(SimilarContentButtonText));
        if (!preserveSearchContext)
        {
            _hasSearched = false;
            _activeSearchQuery = string.Empty;
            ClearSimilarSearchContext();
            _currentSearchPage = 1;
            _totalSearchHits = 0;
            _totalSearchPages = 1;
            RefreshSearchPageNumbers();
            NotifySearchPagingState();
            OnPropertyChanged(nameof(CanFindSimilar));
            OnPropertyChanged(nameof(SimilarContentButtonText));
        }

        NotifyCommandStates();
    }

    private void ClearSimilarSearchContext()
    {
        _isShowingCompatibleAlternatives = false;
        _similarContentSourceTitle = string.Empty;
        _similarContentCriteria = string.Empty;
        _similarContentEnvironments = [];
        IsShowingSimilarContent = false;
        OnPropertyChanged(nameof(SearchResultsTitle));
        OnPropertyChanged(nameof(SimilarContentExplanation));
    }

    private void RefreshSearchPageNumbers()
    {
        SearchPageNumbers.Clear();
        var first = Math.Max(1, CurrentSearchPage - 2);
        var last = Math.Min(TotalSearchPages, first + 4);
        first = Math.Max(1, last - 4);
        for (var page = first; page <= last; page++)
        {
            SearchPageNumbers.Add(page);
        }
    }

    private void NotifySearchPagingState()
    {
        OnPropertyChanged(nameof(SearchPageSummary));
        OnPropertyChanged(nameof(CanGoToPreviousSearchPage));
        OnPropertyChanged(nameof(CanGoToNextSearchPage));
        OnPropertyChanged(nameof(CanShowMore));
        PreviousSearchPageCommand.NotifyCanExecuteChanged();
        NextSearchPageCommand.NotifyCanExecuteChanged();
        ShowMoreCommand.NotifyCanExecuteChanged();
    }

    private static bool CategoriesMatch(string category, ModpackFilterOption filter)
    {
        static string Normalise(string value) => new(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        var normalisedCategory = Normalise(category);
        return normalisedCategory.Equals(Normalise(filter.Id), StringComparison.Ordinal)
            || normalisedCategory.Equals(Normalise(filter.DisplayName), StringComparison.Ordinal);
    }

    private ModpackFilterOption? FindDefiningCategory(ServerContentProject? project)
    {
        if (project is null)
        {
            return null;
        }

        return CategoryFilters
            .Where(filter => project.Categories.Any(category => CategoriesMatch(category, filter)))
            .OrderBy(filter =>
            {
                var priority = Array.IndexOf(SimilarityCategoryPriority, filter.Id);
                return priority < 0 ? int.MaxValue : priority;
            })
            .ThenBy(filter => filter.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> GetSimilarityEnvironments(
        IReadOnlyList<string> sourceEnvironments)
    {
        static bool Matches(string environment, params string[] values) => values.Contains(
            environment,
            StringComparer.OrdinalIgnoreCase);

        var supportsClient = sourceEnvironments.Any(environment => Matches(
            environment,
            "singleplayer_only",
            "client_only",
            "client_only_server_optional",
            "client_and_server",
            "client_or_server",
            "client_or_server_prefers_both"));
        var supportsServer = sourceEnvironments.Any(environment => Matches(
            environment,
            "dedicated_server_only",
            "server_only",
            "server_only_client_optional",
            "client_and_server",
            "client_or_server",
            "client_or_server_prefers_both"));

        if (supportsClient && supportsServer)
        {
            return ["client_and_server", "client_or_server", "client_or_server_prefers_both"];
        }

        if (supportsClient)
        {
            return
            [
                "singleplayer_only",
                "client_only",
                "client_only_server_optional",
                "client_and_server",
                "client_or_server",
                "client_or_server_prefers_both"
            ];
        }

        if (supportsServer)
        {
            return
            [
                "dedicated_server_only",
                "server_only",
                "server_only_client_optional",
                "client_and_server",
                "client_or_server",
                "client_or_server_prefers_both"
            ];
        }

        return [];
    }

    private static int GetAlternativeReferenceScore(ServerContentProject project, string query)
    {
        static string Normalise(string value) => new(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        static IReadOnlySet<string> Tokens(string value) => value
            .Split(
                [' ', '-', '_', ':', '/', '\\', '.', ',', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalise)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

        var normalisedQuery = Normalise(query);
        var normalisedTitle = Normalise(project.Title);
        var normalisedSlug = Normalise(project.Slug);
        if (normalisedQuery.Length > 0
            && (normalisedTitle.Equals(normalisedQuery, StringComparison.Ordinal)
                || normalisedSlug.Equals(normalisedQuery, StringComparison.Ordinal)))
        {
            return 10_000;
        }

        var score = 0;
        if (normalisedQuery.Length > 0
            && (normalisedTitle.Contains(normalisedQuery, StringComparison.Ordinal)
                || normalisedSlug.Contains(normalisedQuery, StringComparison.Ordinal)))
        {
            score += 5_000;
        }

        var queryTokens = Tokens(query);
        var projectTokens = Tokens($"{project.Title} {project.Slug} {project.Description}");
        score += queryTokens.Count(token => projectTokens.Contains(token)) * 250;
        return score;
    }

    private Task ClearDraftAsync()
    {
        DraftItems.Clear();
        OnPropertyChanged(nameof(DraftCountText));
        DraftStatusText = "Draft cleared. No files were changed.";
        ClearDraftCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
        return Task.CompletedTask;
    }

    private void UpdateOutputProgress(PackOutputProgress progress)
    {
        OutputStatusText = progress.Message;
        IsOutputProgressIndeterminate = progress.Percent is null;
        OutputProgressPercent = progress.Percent ?? 0;
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
        FindSimilarCommand.NotifyCanExecuteChanged();
        NotifySearchPagingState();
        ClearDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanReviewAdd));
        OnPropertyChanged(nameof(IsRuntimePairCompatible));
        OnPropertyChanged(nameof(CanCreateOutput));
        OnPropertyChanged(nameof(CanRegisterLastClientOutput));
    }
}
