using System.Collections.ObjectModel;
using System.ComponentModel;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ServerDashboardViewModel : BindableBase
{
    private readonly IServerConfigurationService _configurationService;
    private readonly IServerConfigurationEditorService _editorService;
    private readonly List<ServerConfigurationFile> _allFiles = [];

    private ServerSessionViewModel? _session;
    private ServerConfigurationFile? _selectedConfigurationFile;
    private ServerConfigurationDocument? _document;
    private ServerConfigurationFriendlyDocument? _friendlyDocument;
    private string _documentText = string.Empty;
    private string _originalText = string.Empty;
    private string _searchText = string.Empty;
    private string _environmentText = "Select a server profile to scan its configuration.";
    private string _summaryText = "No configuration scan has run yet.";
    private string _statusText = "Open the dashboard to scan the selected server.";
    private string _selectedFileDetails = "Select a configuration file.";
    private bool _isBusy;
    private bool _isDirty;
    private bool _updatingDocumentText;
    private bool _canUseFriendlyEditor;
    private bool _isFriendlyMode;
    private string _friendlyEditorSummary = "Text Editor required";
    private string _friendlyEditorGuidance = "Select a supported configuration file to use friendly controls.";
    private long _profileRevision;
    private long _documentRevision;

    public ServerDashboardViewModel(
        IServerConfigurationService configurationService,
        IServerConfigurationEditorService editorService)
    {
        _configurationService = configurationService;
        _editorService = editorService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        DiscardCommand = new AsyncRelayCommand(DiscardAsync, CanDiscard);
        ReloadFileCommand = new AsyncRelayCommand(ReloadSelectedFileAsync, CanReloadFile);
    }

    public ObservableCollection<ServerConfigurationFile> ConfigurationFiles { get; } = [];

    public ObservableCollection<ServerConfigurationSourceStatus> ConfigurationSources { get; } = [];

    public ObservableCollection<ServerConfigurationField> FriendlySettings { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DiscardCommand { get; }

    public AsyncRelayCommand ReloadFileCommand { get; }

    public ServerConfigurationFile? SelectedConfigurationFile
    {
        get => _selectedConfigurationFile;
        set
        {
            if (ReferenceEquals(_selectedConfigurationFile, value))
            {
                return;
            }

            if (IsDirty)
            {
                StatusText = "Save or discard the current changes before opening another file.";
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedConfigurationFile, value))
            {
                _ = LoadSelectedFileAsync();
            }
        }
    }

    public string DocumentText
    {
        get => _documentText;
        set
        {
            if (!SetProperty(ref _documentText, value))
            {
                return;
            }

            if (!_updatingDocumentText && _document is not null)
            {
                IsDirty = !string.Equals(value, _originalText, StringComparison.Ordinal);
                StatusText = IsDirty
                    ? "Unsaved changes. The previous file will be backed up when you save."
                    : "No unsaved changes.";
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public string EnvironmentText
    {
        get => _environmentText;
        private set => SetProperty(ref _environmentText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectedFileDetails
    {
        get => _selectedFileDetails;
        private set => SetProperty(ref _selectedFileDetails, value);
    }

    public bool CanUseFriendlyEditor
    {
        get => _canUseFriendlyEditor;
        private set => SetProperty(ref _canUseFriendlyEditor, value);
    }

    public bool IsFriendlyMode
    {
        get => _isFriendlyMode;
        set
        {
            if (value)
            {
                RebuildFriendlyEditor(selectFriendlyMode: false);
                if (!CanUseFriendlyEditor)
                {
                    StatusText = FriendlyEditorGuidance;
                    return;
                }
            }

            if (SetProperty(ref _isFriendlyMode, value))
            {
                OnPropertyChanged(nameof(EditorModeName));
            }
        }
    }

    public string EditorModeName => IsFriendlyMode ? "User Friendly" : "Text Editor";

    public string FriendlyEditorSummary
    {
        get => _friendlyEditorSummary;
        private set => SetProperty(ref _friendlyEditorSummary, value);
    }

    public string FriendlyEditorGuidance
    {
        get => _friendlyEditorGuidance;
        private set => SetProperty(ref _friendlyEditorGuidance, value);
    }

    public bool HasFriendlyValidationErrors => FriendlySettings.Any(setting => !setting.IsValid);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
                OnPropertyChanged(nameof(IsEditorReadOnly));
                OnPropertyChanged(nameof(IsEditorEditable));
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                NotifyCommandStates();
                OnPropertyChanged(nameof(EditorNotice));
            }
        }
    }

    public bool IsEditorReadOnly => IsBusy || _document is null || IsServerActive;

    public bool IsEditorEditable => !IsEditorReadOnly;

    public string EditorNotice
    {
        get
        {
            if (_document is null)
            {
                return "Select an editable configuration file from the list.";
            }

            if (IsServerActive)
            {
                return $"Stop {_session?.DisplayName ?? "the server"} before changing configuration files.";
            }

            return IsDirty
                ? "Unsaved changes"
                : $"{EncodingDisplayName(_document)} • a backup is created before every save";
        }
    }

    private bool IsServerActive => _session?.IsServerActive == true;

    public async Task SelectProfileAsync(ServerSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.PropertyChanged -= Session_PropertyChanged;
        }

        _session = session;
        _session.PropertyChanged += Session_PropertyChanged;
        _profileRevision++;
        ResetDocument();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var session = _session;
        if (session is null || IsBusy)
        {
            return;
        }

        if (IsDirty)
        {
            StatusText = "Save or discard the current changes before rescanning configuration files.";
            return;
        }

        var revision = _profileRevision;
        IsBusy = true;
        StatusText = $"Scanning {session.DisplayName} for profile-defined configuration sources…";
        try
        {
            var result = await _configurationService.DiscoverAsync(session.Profile);
            if (revision != _profileRevision || !ReferenceEquals(session, _session))
            {
                return;
            }

            ResetDocument();
            _allFiles.Clear();
            _allFiles.AddRange(result.Files);

            ConfigurationSources.Clear();
            foreach (var source in result.Sources)
            {
                ConfigurationSources.Add(source);
            }

            UpdateEnvironment(result);
            ApplyFilter();
            StatusText = result.Files.Count == 0
                ? "No editable files matched this profile's configuration-source definitions."
                : "Configuration scan complete.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _allFiles.Clear();
            ConfigurationFiles.Clear();
            ConfigurationSources.Clear();
            EnvironmentText = "Configuration scan unavailable";
            SummaryText = "The selected server folder could not be scanned.";
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (revision == _profileRevision && ConfigurationFiles.Count > 0)
        {
            SelectedConfigurationFile = ConfigurationFiles.FirstOrDefault(file =>
                file.Name.Equals("server.properties", StringComparison.OrdinalIgnoreCase))
                ?? ConfigurationFiles[0];
        }
    }

    private async Task LoadSelectedFileAsync()
    {
        var session = _session;
        var file = SelectedConfigurationFile;
        if (session is null || file is null)
        {
            ResetDocument(keepSelection: true);
            return;
        }

        var revision = ++_documentRevision;
        IsBusy = true;
        StatusText = $"Opening {file.RelativePath}…";
        try
        {
            var document = await _configurationService.ReadAsync(session.Profile, file);
            if (revision != _documentRevision || !ReferenceEquals(file, SelectedConfigurationFile))
            {
                return;
            }

            _document = document;
            _originalText = document.Content;
            SetDocumentText(document.Content);
            IsDirty = false;
            RebuildFriendlyEditor(selectFriendlyMode: true);
            SelectedFileDetails =
                $"{document.File.SourceName} • {document.File.RelativePath} • {document.File.SizeText} • {EncodingDisplayName(document)}";
            StatusText = IsServerActive
                ? "This file is read-only while the server is active."
                : "Ready to edit. Saving creates a profile-scoped backup first.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _document = null;
            _originalText = string.Empty;
            SetDocumentText(string.Empty);
            IsDirty = false;
            ClearFriendlyEditor();
            SelectedFileDetails = file.RelativePath;
            StatusText = $"This file could not be opened safely: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEditorReadOnly));
            OnPropertyChanged(nameof(IsEditorEditable));
            OnPropertyChanged(nameof(EditorNotice));
            NotifyCommandStates();
        }
    }

    private async Task SaveAsync()
    {
        var session = _session;
        var document = _document;
        if (session is null || document is null || !CanSave())
        {
            return;
        }

        IsBusy = true;
        StatusText = $"Validating and saving {document.File.RelativePath}…";
        try
        {
            var result = await _configurationService.SaveAsync(session.Profile, document, DocumentText);
            _document = result.Document;
            _originalText = result.Document.Content;
            ReplaceFile(result.Document.File);
            IsDirty = false;
            RebuildFriendlyEditor(selectFriendlyMode: IsFriendlyMode);
            SelectedFileDetails =
                $"{result.Document.File.SourceName} • {result.Document.File.RelativePath} • {result.Document.File.SizeText} • {EncodingDisplayName(result.Document)}";
            StatusText = $"Saved {result.Document.File.Name}. The previous version was backed up to {result.BackupPath}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusText = $"The file was not saved: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(EditorNotice));
            NotifyCommandStates();
        }
    }

    private Task DiscardAsync()
    {
        if (_document is null)
        {
            return Task.CompletedTask;
        }

        SetDocumentText(_originalText);
        IsDirty = false;
        RebuildFriendlyEditor(selectFriendlyMode: IsFriendlyMode);
        StatusText = "Unsaved changes were discarded.";
        return Task.CompletedTask;
    }

    private async Task ReloadSelectedFileAsync()
    {
        if (SelectedConfigurationFile is null)
        {
            return;
        }

        IsDirty = false;
        await LoadSelectedFileAsync();
    }

    private void ApplyFilter()
    {
        var filter = SearchText.Trim();
        var desired = string.IsNullOrEmpty(filter)
            ? _allFiles
            : _allFiles.Where(file =>
                file.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || file.RelativePath.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || file.SourceName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || file.Category.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        ConfigurationFiles.Clear();
        foreach (var file in desired)
        {
            ConfigurationFiles.Add(file);
        }

        SummaryText = _allFiles.Count == 0
            ? "No editable configuration files detected"
            : string.IsNullOrEmpty(filter)
                ? FileCountText(_allFiles.Count)
                : $"{FileCountText(ConfigurationFiles.Count)} shown • {_allFiles.Count:N0} detected";
    }

    private void UpdateEnvironment(ServerConfigurationDiscoveryResult result)
    {
        var hasMods = result.Sources.Any(source =>
            source.IsPresent && source.Category.Equals("Mods", StringComparison.OrdinalIgnoreCase));
        var hasPlugins = result.Sources.Any(source =>
            source.IsPresent && source.Category.Equals("Plugins", StringComparison.OrdinalIgnoreCase));

        EnvironmentText = (hasMods, hasPlugins) switch
        {
            (true, true) => "Hybrid server detected • mod and plugin configuration",
            (true, false) => "Modded server detected • mod configuration",
            (false, true) => "Plugin server detected • plugin configuration",
            _ => "Core server configuration"
        };
        SummaryText = FileCountText(result.Files.Count);
    }

    private void ReplaceFile(ServerConfigurationFile updated)
    {
        var allIndex = _allFiles.FindIndex(file => file.FullPath.Equals(
            updated.FullPath,
            StringComparison.OrdinalIgnoreCase));
        if (allIndex >= 0)
        {
            _allFiles[allIndex] = updated;
        }

        var visible = ConfigurationFiles.FirstOrDefault(file => file.FullPath.Equals(
            updated.FullPath,
            StringComparison.OrdinalIgnoreCase));
        if (visible is not null)
        {
            var visibleIndex = ConfigurationFiles.IndexOf(visible);
            ConfigurationFiles[visibleIndex] = updated;
        }

        _selectedConfigurationFile = updated;
        OnPropertyChanged(nameof(SelectedConfigurationFile));
    }

    private void ResetDocument(bool keepSelection = false)
    {
        _documentRevision++;
        _document = null;
        _originalText = string.Empty;
        SetDocumentText(string.Empty);
        IsDirty = false;
        ClearFriendlyEditor();
        SelectedFileDetails = "Select a configuration file.";
        if (!keepSelection)
        {
            _selectedConfigurationFile = null;
            OnPropertyChanged(nameof(SelectedConfigurationFile));
        }

        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(IsEditorEditable));
        OnPropertyChanged(nameof(EditorNotice));
        NotifyCommandStates();
    }

    private void SetDocumentText(string value)
    {
        _updatingDocumentText = true;
        DocumentText = value;
        _updatingDocumentText = false;
    }

    private void RebuildFriendlyEditor(bool selectFriendlyMode)
    {
        ClearFriendlyEditor();
        var session = _session;
        var file = SelectedConfigurationFile;
        if (session is null || file is null || _document is null)
        {
            return;
        }

        _friendlyDocument = _editorService.Parse(session.Profile, file, DocumentText);
        foreach (var field in _friendlyDocument.Fields)
        {
            field.ValueChanged += FriendlyField_ValueChanged;
            FriendlySettings.Add(field);
        }

        CanUseFriendlyEditor = FriendlySettings.Count > 0;
        FriendlyEditorSummary = _friendlyDocument.Summary;
        FriendlyEditorGuidance = _friendlyDocument.Guidance;
        if (selectFriendlyMode && CanUseFriendlyEditor)
        {
            _isFriendlyMode = true;
            OnPropertyChanged(nameof(IsFriendlyMode));
            OnPropertyChanged(nameof(EditorModeName));
        }

        OnPropertyChanged(nameof(HasFriendlyValidationErrors));
    }

    private void ClearFriendlyEditor()
    {
        foreach (var field in FriendlySettings)
        {
            field.ValueChanged -= FriendlyField_ValueChanged;
        }

        FriendlySettings.Clear();
        _friendlyDocument = null;
        CanUseFriendlyEditor = false;
        FriendlyEditorSummary = "Text Editor required";
        FriendlyEditorGuidance = "This file does not expose scalar settings that can be changed safely with friendly controls.";
        _isFriendlyMode = false;
        OnPropertyChanged(nameof(IsFriendlyMode));
        OnPropertyChanged(nameof(EditorModeName));
        OnPropertyChanged(nameof(HasFriendlyValidationErrors));
    }

    private void FriendlyField_ValueChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(HasFriendlyValidationErrors));
        NotifyCommandStates();
        if (_friendlyDocument is null)
        {
            return;
        }

        var invalid = FriendlySettings.FirstOrDefault(field => !field.IsValid);
        if (invalid is not null)
        {
            StatusText = $"{invalid.DisplayName}: {invalid.ValidationText}";
            return;
        }

        try
        {
            DocumentText = _editorService.Apply(_friendlyDocument);
            StatusText = "Friendly setting changed. Review it, then choose Save safely.";
        }
        catch (InvalidDataException exception)
        {
            StatusText = exception.Message;
        }
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ServerSessionViewModel.State)
            or nameof(ServerSessionViewModel.IsServerActive))
        {
            OnPropertyChanged(nameof(IsEditorReadOnly));
            OnPropertyChanged(nameof(IsEditorEditable));
            OnPropertyChanged(nameof(EditorNotice));
            NotifyCommandStates();
            if (IsServerActive && _document is not null)
            {
                StatusText = "Configuration editing is paused while this server is active.";
            }
        }
    }

    private bool CanRefresh() => _session is not null && !IsBusy;

    private bool CanSave() =>
        _document is not null
        && IsDirty
        && !IsBusy
        && !IsServerActive
        && !HasFriendlyValidationErrors;

    private bool CanDiscard() => _document is not null && IsDirty && !IsBusy;

    private bool CanReloadFile() => SelectedConfigurationFile is not null && !IsBusy;

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        ReloadFileCommand.NotifyCanExecuteChanged();
    }

    private static string FileCountText(int count) =>
        count == 1 ? "1 editable configuration file" : $"{count:N0} editable configuration files";

    private static string EncodingDisplayName(ServerConfigurationDocument document) =>
        document.EncodingKind switch
        {
            "utf-16le" => "UTF-16 LE",
            "utf-16be" => "UTF-16 BE",
            _ => document.HasByteOrderMark ? "UTF-8 with BOM" : "UTF-8"
        };
}
