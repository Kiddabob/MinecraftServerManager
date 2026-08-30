using System.Collections.ObjectModel;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ModpackCatalogViewModel : BindableBase
{
    private readonly IModpackCatalogService _catalogService;
    private readonly IModpackImportService _importService;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly IModpackInstallLocationService _installLocationService;
    private string _searchText = string.Empty;
    private string _statusText = "Search Modrinth, Technic, and Feed The Beast together.";
    private bool _isBusy;
    private bool _hasSearched;
    private ModpackCatalogItem? _selectedPack;
    private ModpackCatalogVersion? _selectedVersion;
    private int _versionRequestId;
    private bool _isImporting;
    private string _importStatusText = "Choose a server-compatible version, then install it to managed instances or pick a custom folder.";
    private double _importProgressValue;
    private bool _isImportProgressIndeterminate;
    private string _lastImportedDirectory = string.Empty;

    public ModpackCatalogViewModel(
        IModpackCatalogService catalogService,
        IModpackImportService importService,
        IJavaRuntimeService javaRuntimeService,
        IModpackInstallLocationService installLocationService)
    {
        _catalogService = catalogService;
        _importService = importService;
        _javaRuntimeService = javaRuntimeService;
        _installLocationService = installLocationService;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy && !IsImporting);
    }

    public ObservableCollection<ModpackCatalogItem> Results { get; } = [];

    public ObservableCollection<ModpackCatalogVersion> Versions { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SearchCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanImport));
            }
        }
    }

    public ModpackCatalogItem? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (!SetProperty(ref _selectedPack, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedPack));
            OnPropertyChanged(nameof(CanImport));
            _ = LoadVersionsAsync(value);
        }
    }

    public bool HasSelectedPack => SelectedPack is not null;

    public ModpackCatalogVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(HasSelectedVersion));
                OnPropertyChanged(nameof(JavaRecommendationText));
                OnPropertyChanged(nameof(CanImport));
                if (!IsImporting)
                {
                    ImportStatusText = value is null
                        ? "Choose a server-compatible version, then install it to managed instances or pick a custom folder."
                        : value.ImportReadinessText;
                    ImportProgressValue = 0;
                    IsImportProgressIndeterminate = false;
                    LastImportedDirectory = string.Empty;
                }
            }
        }
    }

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                SearchCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanImport));
                OnPropertyChanged(nameof(ImportButtonText));
            }
        }
    }

    public bool CanImport =>
        !IsBusy
        && !IsImporting
        && SelectedPack is not null
        && SelectedVersion is not null
        && _importService.CanImport(SelectedPack, SelectedVersion);

    public string ImportButtonText => IsImporting
        ? "Installing server pack…"
        : "Install to managed instances";

    public string ManagedInstancesDirectory => _installLocationService.ManagedInstancesDirectory;

    public string ImportStatusText
    {
        get => _importStatusText;
        private set => SetProperty(ref _importStatusText, value);
    }

    public double ImportProgressValue
    {
        get => _importProgressValue;
        private set => SetProperty(ref _importProgressValue, value);
    }

    public bool IsImportProgressIndeterminate
    {
        get => _isImportProgressIndeterminate;
        private set => SetProperty(ref _isImportProgressIndeterminate, value);
    }

    public string LastImportedDirectory
    {
        get => _lastImportedDirectory;
        private set
        {
            if (SetProperty(ref _lastImportedDirectory, value))
            {
                OnPropertyChanged(nameof(HasImportedDirectory));
            }
        }
    }

    public bool HasImportedDirectory => !string.IsNullOrWhiteSpace(LastImportedDirectory);

    public string JavaRecommendationText
    {
        get
        {
            var minecraftVersion = SelectedVersion?.MinecraftVersions.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(minecraftVersion))
            {
                return "Recommended Java: select a published version.";
            }

            var javaMajor = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion);
            return javaMajor is null
                ? $"Recommended Java: review required for Minecraft {minecraftVersion}."
                : $"Recommended Java: Java {javaMajor} for Minecraft {minecraftVersion}.";
        }
    }

    public void EnsureLoaded()
    {
        if (!_hasSearched && SearchCommand.CanExecute(null))
        {
            SearchCommand.Execute(null);
        }
    }

    public async Task<ModpackImportResult?> ImportAsync(string destinationParentDirectory)
    {
        if (!CanImport || SelectedPack is null || SelectedVersion is null)
        {
            ImportStatusText = SelectedVersion?.ImportReadinessText
                ?? "Choose a server-compatible version first.";
            return null;
        }

        IsImporting = true;
        ImportProgressValue = 0;
        IsImportProgressIndeterminate = true;
        LastImportedDirectory = string.Empty;
        var progress = new Progress<ModpackImportProgress>(UpdateImportProgress);
        try
        {
            var result = await _importService.ImportAsync(
                SelectedPack,
                SelectedVersion,
                destinationParentDirectory,
                progress);
            LastImportedDirectory = result.ServerDirectory;
            ImportStatusText = $"{result.ProfileImport.Message} Folder: {result.ServerDirectory}";
            ImportProgressValue = 100;
            IsImportProgressIndeterminate = false;
            return result;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException
                or JsonException or InvalidDataException or InvalidOperationException
                or ArgumentException or NotSupportedException or OverflowException
                or TimeoutException or System.Xml.XmlException
                or System.ComponentModel.Win32Exception)
        {
            ImportStatusText = $"The server pack could not be installed: {exception.Message}";
            ImportProgressValue = 0;
            IsImportProgressIndeterminate = false;
            return null;
        }
        finally
        {
            IsImporting = false;
        }
    }

    public async Task<ModpackImportResult?> ImportToManagedInstancesAsync()
    {
        try
        {
            return await ImportAsync(_installLocationService.EnsureManagedInstancesDirectory());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            ImportStatusText = $"The managed instances folder could not be prepared: {exception.Message}";
            return null;
        }
    }

    private async Task SearchAsync()
    {
        IsBusy = true;
        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? "Loading featured packs from the available providers…"
            : $"Searching all available providers for “{SearchText.Trim()}”…";
        try
        {
            var page = await _catalogService.SearchAsync(SearchText);
            _hasSearched = true;
            Results.Clear();
            foreach (var item in page.Items)
            {
                Results.Add(item);
            }

            var availableProviders = page.ProviderStatuses
                .Where(status => status.Succeeded)
                .Select(status => status.ProviderName)
                .ToArray();
            var unavailableProviders = page.ProviderStatuses
                .Where(status => !status.Succeeded)
                .Select(status => status.ProviderName)
                .ToArray();
            var providerText = availableProviders.Length == 0
                ? "no providers"
                : string.Join(", ", availableProviders);
            StatusText = Results.Count switch
            {
                0 => $"No modpacks matched across {providerText}.",
                1 => $"1 modpack found across {providerText}.",
                _ => $"{Results.Count:N0} modpacks shown across {providerText}."
            };
            if (unavailableProviders.Length > 0)
            {
                StatusText += $" Temporarily unavailable: {string.Join(", ", unavailableProviders)}.";
            }

            SelectedPack = Results.FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
                or InvalidDataException or ArgumentException)
        {
            StatusText = $"The modpack providers could not be searched: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadVersionsAsync(ModpackCatalogItem? pack)
    {
        var requestId = Interlocked.Increment(ref _versionRequestId);
        Versions.Clear();
        SelectedVersion = null;
        if (pack is null)
        {
            return;
        }

        StatusText = $"Loading published versions of {pack.Title}…";
        try
        {
            var versions = await _catalogService.GetVersionsAsync(pack);
            if (requestId != _versionRequestId || !ReferenceEquals(pack, SelectedPack))
            {
                return;
            }

            foreach (var version in versions.Take(50))
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault(version =>
                    _importService.CanImport(pack, version))
                ?? Versions.FirstOrDefault();
            StatusText = Versions.Count switch
            {
                0 => $"{pack.ProviderName} lists no public versions of {pack.Title} for review.",
                1 => $"1 {pack.ProviderName} version of {pack.Title} is available for review.",
                _ => $"{Versions.Count} recent {pack.ProviderName} versions of {pack.Title} are available for review."
            };
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
                or InvalidDataException or ArgumentException)
        {
            if (requestId == _versionRequestId)
            {
                StatusText = $"Versions for {pack.Title} could not be loaded: {exception.Message}";
            }
        }
    }

    private void UpdateImportProgress(ModpackImportProgress progress)
    {
        ImportStatusText = progress.Message;
        IsImportProgressIndeterminate = progress.Percent is null;
        if (progress.Percent is { } percent)
        {
            ImportProgressValue = percent;
        }
    }
}
