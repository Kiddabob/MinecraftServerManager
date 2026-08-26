using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;
using MinecraftServerManager.ViewModels;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MinecraftServerManager.Views;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1220;
    private const int DefaultWindowHeight = 800;
    private const int MinimumWindowWidth = 900;
    private const int MinimumWindowHeight = 600;

    private static readonly string[] AccentBrushResourceKeys =
    [
        "AppAccentBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "NavigationViewSelectionIndicatorForeground",
        "ListViewItemSelectionIndicatorForeground",
        "CheckBoxBackgroundChecked",
        "CheckBoxBackgroundCheckedPointerOver",
        "CheckBoxBackgroundCheckedPressed",
        "CheckBoxBackgroundCheckedDisabled"
    ];

    private bool _initialized;
    private bool _allowClose;
    private bool _stopBeforeCloseInProgress;
    private bool _updatingNavigationSelection;
    private bool _consoleScrollQueued;
    private bool _restoreMaximizedOnLoaded;
    private ObservableCollection<ServerLogEntry>? _observedConsoleEntries;
    private readonly IWindowPlacementService _windowPlacementService;
    private readonly DispatcherQueueTimer _windowPlacementSaveTimer;
    private WindowPlacement? _lastRestoredPlacement;

    public MainWindow(
        MainViewModel viewModel,
        IWindowPlacementService windowPlacementService)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _windowPlacementService = windowPlacementService;
        _windowPlacementService.Load();
        RootGrid.DataContext = ViewModel;
        Title = "Minecraft Server Manager";
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinimumWindowWidth;
            presenter.PreferredMinimumHeight = MinimumWindowHeight;
        }

        AppWindow.Resize(new SizeInt32(DefaultWindowWidth, DefaultWindowHeight));

        _windowPlacementSaveTimer = DispatcherQueue.CreateTimer();
        _windowPlacementSaveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _windowPlacementSaveTimer.IsRepeating = false;
        _windowPlacementSaveTimer.Tick += WindowPlacementSaveTimer_Tick;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
    }

    public MainViewModel ViewModel { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        RestoreWindowPlacement();
        if (_restoreMaximizedOnLoaded && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            _restoreMaximizedOnLoaded = false;
            DispatcherQueue.TryEnqueue(() => presenter.Maximize());
        }

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ObserveSelectedConsole();
        ApplyAppearance();
        UpdatePaneFooterVisibility();
    }

    private void MainNavigationView_PaneOpened(NavigationView sender, object args)
    {
        UpdatePaneFooterVisibility();
    }

    private void MainNavigationView_PaneClosed(NavigationView sender, object args)
    {
        UpdatePaneFooterVisibility();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(UpdatePaneFooterVisibility);
    }

    private void UpdatePaneFooterVisibility()
    {
        ProfilePaneFooter.Visibility = MainNavigationView.IsPaneOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MainNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_updatingNavigationSelection)
        {
            return;
        }

        var destination = args.SelectedItemContainer?.Tag?.ToString() ?? "overview";

        sender.DispatcherQueue.TryEnqueue(() =>
        {
            _updatingNavigationSelection = true;
            foreach (var item in sender.MenuItems.OfType<NavigationViewItem>())
            {
                item.IsSelected = string.Equals(
                    item.Tag?.ToString(),
                    destination,
                    StringComparison.Ordinal);
            }

            foreach (var item in sender.FooterMenuItems.OfType<NavigationViewItem>())
            {
                item.IsSelected = string.Equals(
                    item.Tag?.ToString(),
                    destination,
                    StringComparison.Ordinal);
            }

            _updatingNavigationSelection = false;
        });

        OverviewPage.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DashboardPage.Visibility = destination == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ConsolePage.Visibility = destination == "console" ? Visibility.Visible : Visibility.Collapsed;
        PlayersPage.Visibility = destination == "players" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = destination == "files" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = destination == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (destination == "files")
        {
            ViewModel.RefreshFilesCommand.Execute(null);
        }

        if (destination == "dashboard")
        {
            ViewModel.Dashboard.RefreshCommand.Execute(null);
        }

        if (destination == "console")
        {
            ScrollConsoleToLatest();
        }
    }

    private async void ChooseServerFolder_Click(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "Select server folder"
        };

        var result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            await ViewModel.ImportServerFolderAsync(result.Path);
        }
    }

    private async void ChooseJavaExecutable_Click(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "Select Java executable"
        };
        picker.FileTypeFilter.Add(".exe");

        var result = await picker.PickSingleFileAsync();
        if (result is not null)
        {
            ViewModel.SetProfileJavaExecutable(result.Path);
        }
    }

    private async void InstallManagedJava_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ManagedJavaRuntimeOption option } button
            || option.IsInstalled)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await ViewModel.InstallManagedJavaAsync(option.MajorVersion);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void InstallRecommendedJava_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await ViewModel.InstallRecommendedJavaAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void ServerFilesList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ServerFileItem item)
        {
            await ViewModel.NavigateIntoAsync(item);
        }
    }

    private void AppearanceSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initialized)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearance);
        }
    }

    private void ApplyAppearance()
    {
        RootGrid.RequestedTheme = ViewModel.SelectedThemeOption?.Id switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var accentColor = ViewModel.SelectedAccentOption?.Id == "System"
            ? GetSystemAccentColor()
            : ParseColor(ViewModel.SelectedAccentOption?.HexColor ?? "#60CDFF");

        foreach (var resourceKey in AccentBrushResourceKeys)
        {
            if (Application.Current.Resources[resourceKey] is SolidColorBrush accentBrush)
            {
                accentBrush.Color = WithResourceOpacity(resourceKey, accentColor);
            }
        }

        if (ProfilesList.Resources["ListViewItemSelectionIndicatorForeground"] is SolidColorBrush profileSelectionBrush)
        {
            profileSelectionBrush.Color = accentColor;
        }
    }

    private static Color GetSystemAccentColor()
    {
        return Application.Current.Resources.TryGetValue("SystemAccentColor", out var value)
            && value is Color color
                ? color
                : Colors.DeepSkyBlue;
    }

    private static Color ParseColor(string value)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return Colors.DeepSkyBlue;
        }

        return Color.FromArgb(
            255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
    }

    private static Color WithResourceOpacity(string resourceKey, Color color) => resourceKey switch
    {
        "AccentFillColorSecondaryBrush" or "AccentTextFillColorSecondaryBrush"
            or "CheckBoxBackgroundCheckedPointerOver" =>
            Color.FromArgb(230, color.R, color.G, color.B),
        "AccentFillColorTertiaryBrush" or "AccentTextFillColorTertiaryBrush"
            or "CheckBoxBackgroundCheckedPressed" =>
            Color.FromArgb(204, color.R, color.G, color.B),
        "AccentFillColorDisabledBrush" or "CheckBoxBackgroundCheckedDisabled" =>
            Color.FromArgb(102, color.R, color.G, color.B),
        _ => color
    };

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.SelectedProfile))
        {
            ObserveSelectedConsole();
        }
    }

    private void ObserveSelectedConsole()
    {
        if (_observedConsoleEntries is not null)
        {
            _observedConsoleEntries.CollectionChanged -= ConsoleEntries_CollectionChanged;
        }

        _observedConsoleEntries = ViewModel.SelectedProfile?.ConsoleEntries;
        if (_observedConsoleEntries is not null)
        {
            _observedConsoleEntries.CollectionChanged += ConsoleEntries_CollectionChanged;
        }

        ScrollConsoleToLatest();
    }

    private void ConsoleEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            ScrollConsoleToLatest();
        }
    }

    private void ScrollConsoleToLatest()
    {
        if (_consoleScrollQueued)
        {
            return;
        }

        _consoleScrollQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var entries = ViewModel.SelectedProfile?.ConsoleEntries;
                if (entries is { Count: > 0 })
                {
                    ConsoleLogList.ScrollIntoView(
                        entries[^1],
                        ScrollIntoViewAlignment.Default);
                }
            }
            finally
            {
                _consoleScrollQueued = false;
            }
        }))
        {
            _consoleScrollQueued = false;
        }
    }

    private void CommandTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var command = ViewModel.SelectedProfile?.SendCommand;
        if (args.Key != VirtualKey.Enter || command is null || !command.CanExecute(null))
        {
            return;
        }

        args.Handled = true;
        command.Execute(null);
    }

    private void BroadcastButton_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProfile?.PrepareBroadcast() != true)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            CommandTextBox.Focus(FocusState.Programmatic);
            CommandTextBox.SelectionStart = CommandTextBox.Text.Length;
        });
    }

    private async void EmergencyStopButton_Click(object sender, RoutedEventArgs args)
    {
        var profile = ViewModel.SelectedProfile;
        if (profile?.CanEmergencyStop != true)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Emergency stop this server?",
            Content = "This immediately terminates Java without asking the server to save. Recent world changes can be lost or corrupted. Use Stop safely whenever possible.",
            PrimaryButtonText = "Force stop without saving",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await profile.EmergencyStopAsync();
        }
    }

    private void ResourcePaneSplitter_DragDelta(object sender, DragDeltaEventArgs args)
    {
        const double minimumSidebarWidth = 240d;
        const double minimumConsoleWidth = 320d;
        const double splitterWidth = 12d;

        var maximumSidebarWidth = Math.Max(
            minimumSidebarWidth,
            ConsolePage.ActualWidth - minimumConsoleWidth - splitterWidth);
        var newWidth = Math.Clamp(
            ResourceSidebarColumn.ActualWidth - args.HorizontalChange,
            minimumSidebarWidth,
            Math.Min(520d, maximumSidebarWidth));
        ResourceSidebarColumn.Width = new GridLength(newWidth);
    }

    private void DashboardSplitter_DragDelta(object sender, DragDeltaEventArgs args)
    {
        const double minimumFileListWidth = 240d;
        const double minimumEditorWidth = 360d;
        const double splitterWidth = 12d;

        var maximumFileListWidth = Math.Max(
            minimumFileListWidth,
            DashboardPage.ActualWidth - minimumEditorWidth - splitterWidth);
        var newWidth = Math.Clamp(
            DashboardFileListColumn.ActualWidth + args.HorizontalChange,
            minimumFileListWidth,
            Math.Min(520d, maximumFileListWidth));
        DashboardFileListColumn.Width = new GridLength(newWidth);
    }

    private void RestoreWindowPlacement()
    {
        var saved = _windowPlacementService.Current;
        if (saved is null)
        {
            _lastRestoredPlacement = CaptureRestoredPlacement(isMaximized: false);
            return;
        }

        var savedRect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
        DisplayArea? targetArea = null;
        long largestIntersection = 0;
        var displayAreas = DisplayArea.FindAll();
        for (var index = 0; index < displayAreas.Count; index++)
        {
            var displayArea = displayAreas[index];
            var intersection = CalculateIntersectionArea(savedRect, displayArea.OuterBounds);
            if (intersection > largestIntersection)
            {
                largestIntersection = intersection;
                targetArea = displayArea;
            }
        }

        targetArea ??= DisplayArea.Primary;
        var workArea = targetArea.WorkArea;
        var minimumWidth = Math.Min(MinimumWindowWidth, workArea.Width);
        var minimumHeight = Math.Min(MinimumWindowHeight, workArea.Height);
        var width = Math.Clamp(saved.Width, minimumWidth, workArea.Width);
        var height = Math.Clamp(saved.Height, minimumHeight, workArea.Height);
        var x = largestIntersection > 0
            ? Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - width)
            : workArea.X + ((workArea.Width - width) / 2);
        var y = largestIntersection > 0
            ? Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - height)
            : workArea.Y + ((workArea.Height - height) / 2);

        var restored = new WindowPlacement
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            IsMaximized = saved.IsMaximized
        };
        _lastRestoredPlacement = restored;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        _restoreMaximizedOnLoaded = saved.IsMaximized;
    }

    private static long CalculateIntersectionArea(RectInt32 left, RectInt32 right)
    {
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return (long)width * height;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter restoredPresenter
            && restoredPresenter.State == OverlappedPresenterState.Restored)
        {
            _lastRestoredPlacement = CaptureRestoredPlacement(isMaximized: false);
        }

        _windowPlacementSaveTimer.Stop();
        _windowPlacementSaveTimer.Start();
    }

    private async void WindowPlacementSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await PersistWindowPlacementAsync();
    }

    private WindowPlacement CaptureRestoredPlacement(bool isMaximized)
    {
        return new WindowPlacement
        {
            X = AppWindow.Position.X,
            Y = AppWindow.Position.Y,
            Width = AppWindow.Size.Width,
            Height = AppWindow.Size.Height,
            IsMaximized = isMaximized
        };
    }

    private async Task PersistWindowPlacementAsync()
    {
        var isMaximized = AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized;
        var placement = _lastRestoredPlacement ?? CaptureRestoredPlacement(isMaximized: false);
        placement = placement with { IsMaximized = isMaximized };

        try
        {
            await _windowPlacementService.SaveAsync(placement);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Window placement is a convenience; failure must never block the app from closing.
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_stopBeforeCloseInProgress)
        {
            return;
        }

        _stopBeforeCloseInProgress = true;
        _windowPlacementSaveTimer.Stop();
        await PersistWindowPlacementAsync();
        var stopped = await ViewModel.StopForAppExitAsync();
        _stopBeforeCloseInProgress = false;

        if (stopped)
        {
            _allowClose = true;
            Close();
        }
    }
}
