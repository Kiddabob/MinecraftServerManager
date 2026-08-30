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
    private readonly IPackPlatformVersionService _platformVersionService;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly ICurseForgeApiKeyService _curseForgeApiKeyService;
    private readonly IPackDraftOutputService _outputService;
    private readonly IModpackInstallLocationService _installLocationService;
    private PackBuildTargetOption? _selectedTarget;
    private string? _selectedMinecraftVersion;
    private PackPlatformOption? _selectedClientPlatform;
    private PackPlatformOption? _selectedServerPlatform;
    private PackPlatformVersionOption? _selectedClientRuntimeVersion;
    private PackPlatformVersionOption? _selectedServerRuntimeVersion;
    private ServerContentKindOption? _selectedContentKind;
    private PackCategoryOption? _selectedCategory;
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
    private double _outputProgressPercent;
    private bool _isOutputProgressIndeterminate = true;
    private string _curseForgeConnectionStatusText =
        "No approved CurseForge developer key is stored on this Windows account.";
    private bool _isBusy;
    private bool _isOutputBusy;
    private bool _isCurseForgeConnectionBusy;
    private bool _isCurseForgeApiKeyStored;
    private bool _isResolvingPlatformVersions;
    private bool _isLoaded;
    private bool _isCurseForgeConnectionLoaded;
    private bool _isRefreshingPlatformOptions;
    private int _busyOperationCount;
    private int _versionRequestId;
    private int _platformVersionRequestId;

    public PackBuilderViewModel(
        IPackContentCatalogService catalogService,
        IPackDependencyResolver dependencyResolver,
        IPackPlatformCatalogService platformCatalog,
        IPackPlatformVersionService platformVersionService,
        IJavaRuntimeService javaRuntimeService,
        ICurseForgeApiKeyService curseForgeApiKeyService,
        IPackDraftOutputService outputService,
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
        _installLocationService = installLocationService
            ?? throw new ArgumentNullException(nameof(installLocationService));

        foreach (var option in platformCatalog.GetBuildTargets())
        {
            BuildTargets.Add(option);
        }

        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
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
            OnPropertyChanged(nameof(ShowClientRuntimeVersion));
            OnPropertyChanged(nameof(ShowServerRuntimeVersion));
            OnPropertyChanged(nameof(OutputCapabilityTitle));
            OnPropertyChanged(nameof(OutputCapabilityMessage));
            RefreshContentKinds();
            EnsureContentKindSupported();
            ResetForSetupChange("Build target changed. Search again to create a compatible draft.");
            QueuePlatformVersionRefresh();
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

    public bool HasSelectedProject => SelectedProject is not null;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool CanReviewAdd => SelectedProject is not null && SelectedVersion is not null && !IsBusy;

    public bool CanCreateOutput => (DraftItems.Count > 0 || CanBuildEmptyServerBaseline)
        && DraftItems.All(item => item.Placement != PackContentPlacement.Review)
        && !string.IsNullOrWhiteSpace(PackName)
        && !IsResolvingPlatformVersions
        && (!ShowClientRuntimeVersion || SelectedClientRuntimeVersion is not null)
        && (!ShowServerRuntimeVersion || SelectedServerRuntimeVersion is not null)
        && !IsBusy;

    public bool CanBuildEmptyServerBaseline => SelectedTarget?.Target == PackBuildTarget.Server
        && SelectedServerRuntimeVersion?.CanPrepareServer == true;

    public bool HasLastOutput => !string.IsNullOrWhiteSpace(LastOutputDirectory);

    public string ManagedOutputDirectory => _installLocationService.ManagedInstancesDirectory;

    public string ManagedOutputDirectoryText => $"Managed Instances folder: {ManagedOutputDirectory}";

    public string TargetGuidance => SelectedTarget?.Description ?? "Choose an output.";

    public string ClientPlatformGuidance => SelectedClientPlatform is null
        ? "Choose a client loader."
        : $"{SelectedClientPlatform.KindText}  •  {SelectedClientPlatform.CapabilityText}\n{SelectedClientPlatform.GuidanceText}{MinecraftCompatibilitySuffix}";

    public string ServerPlatformGuidance => SelectedServerPlatform is null
        ? "Choose a server platform."
        : $"{SelectedServerPlatform.KindText}  •  {SelectedServerPlatform.CapabilityText}\n{SelectedServerPlatform.GuidanceText}{MinecraftCompatibilitySuffix}";

    public string OutputCapabilityTitle => SelectedServerRuntimeVersion?.CanPrepareServer == true
        && ShowServerPlatform
            ? "Runnable server output"
            : "Content-only output";

    public string OutputCapabilityMessage => SelectedServerRuntimeVersion?.CanPrepareServer == true
        && ShowServerPlatform
            ? $"The manager will install the exact {SelectedServerPlatform?.DisplayName} {SelectedServerRuntimeVersion.Version} server baseline, then add it as a profile. Java {GetRecommendedJavaText()} is recommended. The Minecraft EULA remains unaccepted until you review it in the app."
            : "Verified mod/plugin files and a manifest will be created. This platform does not yet have a safe runnable baseline installer here, and client launcher authentication is not installed.";

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
        try
        {
            var result = await _outputService.CreateOutputAsync(
                plan,
                new Progress<PackOutputProgress>(UpdateOutputProgress));
            LastOutputDirectory = result.OutputDirectory;
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
        var dependencyCount = addedItems.Count(item => item.IsDependency);
        var additionText = dependencyCount == 0
            ? $"Added {addedItems.Count:N0} selected item{(addedItems.Count == 1 ? string.Empty : "s")}."
            : $"Added the selected item and {dependencyCount:N0} required dependenc{(dependencyCount == 1 ? "y" : "ies")} automatically.";
        var noticeText = plan.Warnings.Count == 0
            ? string.Empty
            : $" Optional/review notice: {string.Join(" ", plan.Warnings.Take(2))}"
                + (plan.Warnings.Count > 2 ? $" (+{plan.Warnings.Count - 2:N0} more)" : string.Empty);
        DraftStatusText = $"{DraftCountText}. {additionText}{noticeText} Planning draft only — nothing has been downloaded or installed.";
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

            ServerPlatforms.Clear();
            foreach (var option in _platformCatalog.GetServerPlatforms(SelectedMinecraftVersion))
            {
                ServerPlatforms.Add(option);
            }

            _selectedClientPlatform = ClientPlatforms.FirstOrDefault(option =>
                    option.Id.Equals(preferredClientId, StringComparison.OrdinalIgnoreCase))
                ?? SelectPreferredClientPlatform();
            _selectedServerPlatform = ServerPlatforms.FirstOrDefault(option =>
                    option.Id.Equals(preferredServerId, StringComparison.OrdinalIgnoreCase))
                ?? SelectPreferredServerPlatform(_selectedClientPlatform);

            OnPropertyChanged(nameof(SelectedClientPlatform));
            OnPropertyChanged(nameof(SelectedServerPlatform));
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
        ClearDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanReviewAdd));
        OnPropertyChanged(nameof(CanCreateOutput));
    }
}
