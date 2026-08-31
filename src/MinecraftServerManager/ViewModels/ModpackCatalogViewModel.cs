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
    private readonly IPackContentCatalogService _contentCatalogService;
    private string _searchText = string.Empty;
    private string _statusText = "Search Modrinth, Technic, Feed The Beast, and connected CurseForge together.";
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
    private ModpackFilterOption? _selectedMinecraftVersion;
    private ModpackFilterOption? _selectedLoader;
    private ModpackPageSizeOption _selectedPageSize;
    private int _currentPage = 1;
    private int _totalHits;
    private int _totalPages = 1;
    private bool _isLoadingFilterMetadata;
    private bool _hasLoadedFilterMetadata;

    public ModpackCatalogViewModel(
        IModpackCatalogService catalogService,
        IModpackImportService importService,
        IJavaRuntimeService javaRuntimeService,
        IModpackInstallLocationService installLocationService,
        IPackContentCatalogService contentCatalogService)
    {
        _catalogService = catalogService;
        _importService = importService;
        _javaRuntimeService = javaRuntimeService;
        _installLocationService = installLocationService;
        _contentCatalogService = contentCatalogService;
        ProviderFilters =
        [
            new("modrinth", "Modrinth", true),
            new("curseforge", "CurseForge", true),
            new("technic", "Technic", true),
            new("ftb", "FTB", true)
        ];
        CategoryFilters =
        [
            new("technology", "Technology"),
            new("magic", "Magic"),
            new("adventure", "Adventure"),
            new("action", "Action & combat"),
            new("space", "Space"),
            new("quests", "Quests"),
            new("optimization", "Performance"),
            new("vanilla-plus", "Vanilla+")
        ];
        LoaderOptions =
        [
            new("", "All loaders"),
            new("forge", "Forge"),
            new("neoforge", "NeoForge"),
            new("fabric", "Fabric"),
            new("quilt", "Quilt"),
            new("liteloader", "LiteLoader"),
            new("cauldron", "Cauldron")
        ];
        MinecraftVersionOptions = [new("", "All Minecraft versions")];
        PageSizeOptions =
        [
            new(20, "20 per page"),
            new(40, "40 per page"),
            new(60, "60 per page")
        ];
        _selectedMinecraftVersion = MinecraftVersionOptions[0];
        _selectedLoader = LoaderOptions[0];
        _selectedPageSize = PageSizeOptions[0];
        SearchCommand = new AsyncRelayCommand(SearchFromStartAsync, () => !IsBusy && !IsImporting);
        PreviousPageCommand = new AsyncRelayCommand(PreviousPageAsync, () => CanGoToPreviousPage);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => CanGoToNextPage);
    }

    public ObservableCollection<ModpackCatalogItem> Results { get; } = [];

    public ObservableCollection<ModpackCatalogVersion> Versions { get; } = [];

    public ObservableCollection<ModpackFilterOption> ProviderFilters { get; }

    public ObservableCollection<ModpackFilterOption> CategoryFilters { get; }

    public ObservableCollection<ModpackFilterOption> MinecraftVersionOptions { get; }

    public ObservableCollection<ModpackFilterOption> LoaderOptions { get; }

    public ObservableCollection<ModpackPageSizeOption> PageSizeOptions { get; }

    public ObservableCollection<int> PageNumbers { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand PreviousPageCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public ModpackFilterOption? SelectedMinecraftVersion
    {
        get => _selectedMinecraftVersion;
        set => SetProperty(ref _selectedMinecraftVersion, value);
    }

    public ModpackFilterOption? SelectedLoader
    {
        get => _selectedLoader;
        set => SetProperty(ref _selectedLoader, value);
    }

    public ModpackPageSizeOption SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (value is not null && SetProperty(ref _selectedPageSize, value) && _hasSearched)
            {
                _ = SearchPageAsync(1);
            }
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                PreviousPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int TotalHits
    {
        get => _totalHits;
        private set
        {
            if (SetProperty(ref _totalHits, value))
            {
                OnPropertyChanged(nameof(PageSummary));
            }
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (SetProperty(ref _totalPages, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(CanGoToNextPage));
                NextPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string PageSummary => TotalHits == 0
        ? "No results"
        : $"Page {CurrentPage:N0} of {TotalPages:N0} • {TotalHits:N0} result{(TotalHits == 1 ? string.Empty : "s")}";

    public bool CanGoToPreviousPage => !IsBusy && CurrentPage > 1;

    public bool CanGoToNextPage => !IsBusy && CurrentPage < TotalPages;

    public bool IsLoadingFilterMetadata
    {
        get => _isLoadingFilterMetadata;
        private set => SetProperty(ref _isLoadingFilterMetadata, value);
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
                PreviousPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanImport));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
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
        if (!_hasLoadedFilterMetadata && !IsLoadingFilterMetadata)
        {
            _ = LoadFilterMetadataAsync();
        }

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

    public Task GoToPageAsync(int pageNumber)
    {
        return pageNumber < 1 || pageNumber > TotalPages || pageNumber == CurrentPage
            ? Task.CompletedTask
            : SearchPageAsync(pageNumber);
    }

    private Task SearchFromStartAsync() => SearchPageAsync(1);

    private Task PreviousPageAsync() => CanGoToPreviousPage
        ? SearchPageAsync(CurrentPage - 1)
        : Task.CompletedTask;

    private Task NextPageAsync() => CanGoToNextPage
        ? SearchPageAsync(CurrentPage + 1)
        : Task.CompletedTask;

    private async Task SearchPageAsync(int pageNumber)
    {
        var selectedProviderIds = ProviderFilters
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        if (selectedProviderIds.Length == 0)
        {
            StatusText = "Choose at least one modpack provider to search.";
            return;
        }

        IsBusy = true;
        var pageSize = SelectedPageSize.Value;
        var offset = Math.Max(0, (pageNumber - 1) * pageSize);
        var selectedCategories = CategoryFilters
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToArray();
        var request = new ModpackCatalogSearchRequest(
            SearchText,
            offset,
            pageSize,
            selectedProviderIds,
            SelectedMinecraftVersion?.Id ?? string.Empty,
            SelectedLoader?.Id ?? string.Empty,
            selectedCategories);
        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? "Loading featured packs from the selected providers…"
            : $"Searching the selected providers for “{SearchText.Trim()}”…";
        try
        {
            var page = await _catalogService.SearchAsync(request);
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
            CurrentPage = pageNumber;
            TotalHits = page.TotalHits;
            TotalPages = page.TotalHits == 0
                ? 1
                : (int)Math.Ceiling(page.TotalHits / (double)pageSize);
            RefreshPageNumbers();
            StatusText = Results.Count switch
            {
                0 => $"No modpacks matched across {providerText}.",
                1 => $"1 modpack found across {providerText}.",
                _ => $"{Results.Count:N0} modpacks shown on page {CurrentPage:N0} across {providerText}."
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

    private async Task LoadFilterMetadataAsync()
    {
        IsLoadingFilterMetadata = true;
        try
        {
            var versions = await _contentCatalogService.GetMinecraftVersionsAsync();
            foreach (var version in versions
                         .Where(version => !string.IsNullOrWhiteSpace(version))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                MinecraftVersionOptions.Add(new ModpackFilterOption(version, $"Minecraft {version}"));
            }

            _hasLoadedFilterMetadata = true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
                or InvalidDataException or ArgumentException)
        {
            _hasLoadedFilterMetadata = true;
        }
        finally
        {
            IsLoadingFilterMetadata = false;
        }
    }

    private void RefreshPageNumbers()
    {
        PageNumbers.Clear();
        var first = Math.Max(1, CurrentPage - 2);
        var last = Math.Min(TotalPages, first + 4);
        first = Math.Max(1, last - 4);
        for (var page = first; page <= last; page++)
        {
            PageNumbers.Add(page);
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

        ImportStatusText = $"Loading published versions of {pack.Title}…";
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
            if (Versions.Count == 0)
            {
                ImportStatusText =
                    $"{pack.ProviderName} lists no public versions of {pack.Title} for review.";
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
                or InvalidDataException or ArgumentException)
        {
            if (requestId == _versionRequestId)
            {
                ImportStatusText =
                    $"Versions for {pack.Title} could not be loaded: {exception.Message}";
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
