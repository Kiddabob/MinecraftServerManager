using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ServerContentViewModel : BindableBase
{
    private readonly IServerContentInventoryService _inventoryService;
    private readonly IServerContentCatalogService _catalogService;
    private readonly IServerContentInstallService _installService;
    private ServerSessionViewModel? _session;
    private ServerContentInventory? _inventory;
    private ServerContentTarget? _selectedTarget;
    private ServerContentProject? _selectedProject;
    private ServerContentVersion? _selectedVersion;
    private string _searchText = string.Empty;
    private string _environmentText = "Select a server profile to inspect its content.";
    private string _targetSummary = "No server content target selected.";
    private string _inventoryStatusText = "Select a server profile to scan installed mods and plugins.";
    private string _searchStatusText = "Choose Mods or Plugins, then search compatible Modrinth projects.";
    private string _installStatusText = "Existing files are never overwritten automatically.";
    private bool _isBusy;
    private bool _isInstalling;
    private double _installProgressValue;
    private bool _isInstallProgressIndeterminate;
    private int _busyOperationCount;
    private int _profileRequestId;
    private int _versionRequestId;

    public ServerContentViewModel(
        IServerContentInventoryService inventoryService,
        IServerContentCatalogService catalogService,
        IServerContentInstallService installService)
    {
        _inventoryService = inventoryService;
        _catalogService = catalogService;
        _installService = installService;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
    }

    public ObservableCollection<ServerContentTarget> Targets { get; } = [];

    public ObservableCollection<ServerContentItem> InstalledItems { get; } = [];

    public ObservableCollection<ServerContentProject> SearchResults { get; } = [];

    public ObservableCollection<ServerContentVersion> Versions { get; } = [];

    public ObservableCollection<ServerContentInstallPlanItem> PlannedItems { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string EnvironmentText
    {
        get => _environmentText;
        private set => SetProperty(ref _environmentText, value);
    }

    public string TargetSummary
    {
        get => _targetSummary;
        private set => SetProperty(ref _targetSummary, value);
    }

    public string InventoryStatusText
    {
        get => _inventoryStatusText;
        private set => SetProperty(ref _inventoryStatusText, value);
    }

    public string SearchStatusText
    {
        get => _searchStatusText;
        private set => SetProperty(ref _searchStatusText, value);
    }

    public string InstallStatusText
    {
        get => _installStatusText;
        private set => SetProperty(ref _installStatusText, value);
    }

    public ServerContentTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            SearchResults.Clear();
            Versions.Clear();
            PlannedItems.Clear();
            SelectedProject = null;
            SelectedVersion = null;
            SearchStatusText = value is null
                ? "This profile does not expose a standard mods or plugins directory."
                : $"Search server-compatible {value.KindText.ToLowerInvariant()} for Minecraft {_inventory?.MinecraftVersion ?? "Unknown"} and {value.LoadersText}.";
            NotifyCommandStates();
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

            OnPropertyChanged(nameof(HasSelectedProject));
            Versions.Clear();
            PlannedItems.Clear();
            SelectedVersion = null;
            _ = LoadVersionsAsync(value);
        }
    }

    public bool HasSelectedProject => SelectedProject is not null;

    public ServerContentVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                PlannedItems.Clear();
                InstallStatusText = value is null
                    ? "Choose a compatible published version."
                    : $"{value.DependencyText}. The complete install plan is resolved before anything is downloaded.";
                OnPropertyChanged(nameof(HasSelectedVersion));
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value))
            {
                NotifyCommandStates();
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(SafetyText));
            }
        }
    }

    public double InstallProgressValue
    {
        get => _installProgressValue;
        private set => SetProperty(ref _installProgressValue, value);
    }

    public bool IsInstallProgressIndeterminate
    {
        get => _isInstallProgressIndeterminate;
        private set => SetProperty(ref _isInstallProgressIndeterminate, value);
    }

    public bool IsServerActive => _session?.IsServerActive == true;

    public string SafetyText => IsInstalling
        ? "Installing verified files. This server is locked until the operation finishes."
        : IsServerActive
            ? "Stop this server before adding mods or plugins."
            : "The server is stopped. Verified additions can be installed safely.";

    public bool CanInstall =>
        SelectedProject is not null
        && SelectedVersion?.PrimaryFile is not null
        && SelectedTarget is not null
        && !IsServerActive
        && !IsBusy
        && !IsInstalling;

    public async Task SelectProfileAsync(ServerSessionViewModel? session)
    {
        if (ReferenceEquals(_session, session))
        {
            await RefreshForProfileSelectionAsync();
            return;
        }

        if (_session is not null)
        {
            _session.PropertyChanged -= Session_PropertyChanged;
        }

        _session = session;
        if (_session is not null)
        {
            _session.PropertyChanged += Session_PropertyChanged;
        }

        Interlocked.Increment(ref _profileRequestId);
        _inventory = null;
        Targets.Clear();
        InstalledItems.Clear();
        SearchResults.Clear();
        Versions.Clear();
        PlannedItems.Clear();
        _selectedTarget = null;
        _selectedProject = null;
        _selectedVersion = null;
        OnPropertyChanged(nameof(SelectedTarget));
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(IsServerActive));
        OnPropertyChanged(nameof(SafetyText));
        OnPropertyChanged(nameof(CanInstall));
        await RefreshForProfileSelectionAsync();
    }

    public void EnsureLoaded()
    {
        if (_session is not null && _inventory is null && RefreshCommand.CanExecute(null))
        {
            RefreshCommand.Execute(null);
        }
    }

    public async Task<ServerContentInstallPlan?> PrepareInstallAsync()
    {
        var session = _session;
        var target = SelectedTarget;
        var project = SelectedProject;
        var version = SelectedVersion;
        if (!CanInstall || session is null || target is null || project is null || version is null)
        {
            return null;
        }

        BeginBusy();
        InstallStatusText = "Resolving required dependencies and checking file collisions…";
        try
        {
            var plan = await _installService.CreatePlanAsync(
                session.Profile,
                target,
                project,
                version);
            if (!ReferenceEquals(session, _session)
                || !Equals(target, SelectedTarget)
                || !ReferenceEquals(project, SelectedProject)
                || !ReferenceEquals(version, SelectedVersion))
            {
                return null;
            }

            PlannedItems.Clear();
            foreach (var item in plan.Items)
            {
                PlannedItems.Add(item);
            }

            InstallStatusText = plan.Warnings.Count == 0
                ? $"{plan.SummaryText}. {plan.TotalBytes / 1024d / 1024d:0.0} MB will be verified before installation."
                : $"{plan.SummaryText}. {plan.Warnings.Count} warning(s) require review.";
            return plan;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            InstallStatusText = $"The install plan could not be created: {exception.Message}";
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task InstallAsync(ServerContentInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var session = _session;
        if (session is null || IsServerActive || IsInstalling
            || !session.TryBeginContentInstallation())
        {
            InstallStatusText = "Stop the selected server before installing content.";
            return;
        }

        IsInstalling = true;
        InstallProgressValue = 0;
        IsInstallProgressIndeterminate = true;
        var progress = new Progress<ServerContentInstallProgress>(value =>
        {
            InstallStatusText = value.Message;
            IsInstallProgressIndeterminate = value.Percent is null;
            InstallProgressValue = value.Percent ?? 0;
        });

        try
        {
            var result = await _installService.InstallAsync(plan, progress);
            InstallStatusText = result.InstalledFileCount == 1
                ? $"Installed and verified {result.InstalledFiles[0]}."
                : $"Installed and verified {result.InstalledFileCount:N0} files.";
            if (ReferenceEquals(session, _session))
            {
                await RefreshInventoryAsync();
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            InstallStatusText = $"Nothing was installed: {exception.Message}";
        }
        finally
        {
            session.EndContentInstallation();
            IsInstalling = false;
            IsInstallProgressIndeterminate = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (_session is null)
        {
            EnvironmentText = "Select a server profile to inspect its content.";
            TargetSummary = "No server content target selected.";
            InventoryStatusText = "Select a server profile to scan installed mods and plugins.";
            return;
        }

        if (IsBusy || IsInstalling)
        {
            return;
        }

        BeginBusy();
        try
        {
            await RefreshInventoryAsync();
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RefreshForProfileSelectionAsync()
    {
        BeginBusy();
        try
        {
            await RefreshInventoryAsync();
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RefreshInventoryAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var requestId = Interlocked.Increment(ref _profileRequestId);
        var previousKind = SelectedTarget?.Kind;
        InventoryStatusText = "Scanning installed mods and plugins…";
        try
        {
            var inventory = await _inventoryService.DiscoverAsync(session.Profile);
            if (requestId != _profileRequestId || !ReferenceEquals(session, _session))
            {
                return;
            }

            _inventory = inventory;
            EnvironmentText = inventory.EnvironmentText;
            TargetSummary = inventory.TargetSummary;
            InventoryStatusText = inventory.ItemCountText;

            SelectedTarget = null;
            Targets.Clear();
            foreach (var target in inventory.Targets)
            {
                Targets.Add(target);
            }

            InstalledItems.Clear();
            foreach (var item in inventory.Items)
            {
                InstalledItems.Add(item);
            }

            SelectedTarget = Targets.FirstOrDefault(target => target.Kind == previousKind)
                ?? Targets.FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (requestId == _profileRequestId)
            {
                InventoryStatusText = $"The server content could not be scanned: {exception.Message}";
            }
        }
    }

    private async Task SearchAsync()
    {
        var inventory = _inventory;
        var target = SelectedTarget;
        if (inventory is null || target is null)
        {
            return;
        }

        BeginBusy();
        SearchStatusText = $"Searching Modrinth for compatible {target.KindText.ToLowerInvariant()}…";
        ServerContentProject? firstProject = null;
        try
        {
            var page = await _catalogService.SearchAsync(
                SearchText,
                inventory.MinecraftVersion,
                target.Kind,
                target.LoaderIds);
            if (!ReferenceEquals(inventory, _inventory) || !Equals(target, SelectedTarget))
            {
                return;
            }

            SearchResults.Clear();
            foreach (var item in page.Items)
            {
                SearchResults.Add(item);
            }

            firstProject = SearchResults.FirstOrDefault();
            SearchStatusText = page.TotalHits switch
            {
                0 => "No compatible server-side projects were found.",
                1 => "1 compatible project found.",
                _ => $"{page.TotalHits:N0} compatible projects found. Showing {SearchResults.Count:N0}."
            };
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            SearchStatusText = $"Modrinth search failed: {exception.Message}";
        }
        finally
        {
            EndBusy();
        }

        SelectedProject = firstProject;
    }

    private async Task LoadVersionsAsync(ServerContentProject? project)
    {
        var inventory = _inventory;
        var target = SelectedTarget;
        var requestId = Interlocked.Increment(ref _versionRequestId);
        Versions.Clear();
        SelectedVersion = null;
        if (project is null || inventory is null || target is null)
        {
            return;
        }

        BeginBusy();
        InstallStatusText = "Loading compatible published versions…";
        try
        {
            var versions = await _catalogService.GetVersionsAsync(
                project.ProjectId,
                inventory.MinecraftVersion,
                target.LoaderIds);
            if (requestId != _versionRequestId || !ReferenceEquals(project, SelectedProject))
            {
                return;
            }

            foreach (var version in versions)
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault();
            InstallStatusText = Versions.Count == 0
                ? "No verified server-side JAR matches this profile's Minecraft version and loader."
                : $"{Versions.Count:N0} compatible published versions available.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            if (requestId == _versionRequestId)
            {
                InstallStatusText = $"Versions could not be loaded: {exception.Message}";
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private void BeginBusy()
    {
        if (Interlocked.Increment(ref _busyOperationCount) == 1)
        {
            IsBusy = true;
        }
    }

    private void EndBusy()
    {
        var remaining = Interlocked.Decrement(ref _busyOperationCount);
        if (remaining <= 0)
        {
            Interlocked.Exchange(ref _busyOperationCount, 0);
            IsBusy = false;
        }
    }

    private bool CanRefresh() => _session is not null && !IsBusy && !IsInstalling;

    private bool CanSearch() =>
        _inventory is not null
        && SelectedTarget is not null
        && !IsBusy
        && !IsInstalling;

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ServerSessionViewModel.State)
            or nameof(ServerSessionViewModel.IsServerActive))
        {
            OnPropertyChanged(nameof(IsServerActive));
            OnPropertyChanged(nameof(SafetyText));
            OnPropertyChanged(nameof(CanInstall));
        }
    }
}
