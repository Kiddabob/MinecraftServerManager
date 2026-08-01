using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using MinecraftServerManager.Models;
using MinecraftServerManager.ViewModels;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace MinecraftServerManager.Views;

public sealed partial class MainWindow : Window
{
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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        RootGrid.DataContext = ViewModel;
        Title = "Minecraft Server Manager";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1220, 800));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        AppWindow.Closing += AppWindow_Closing;
    }

    public MainViewModel ViewModel { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
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
        ConsolePage.Visibility = destination == "console" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = destination == "files" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = destination == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (destination == "files")
        {
            ViewModel.RefreshFilesCommand.Execute(null);
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

    private void ConsoleTextBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        ConsoleTextBox.SelectionStart = ConsoleTextBox.Text.Length;
        ConsoleTextBox.SelectionLength = 0;
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

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !ViewModel.IsServerActive)
        {
            return;
        }

        args.Cancel = true;
        if (_stopBeforeCloseInProgress)
        {
            return;
        }

        _stopBeforeCloseInProgress = true;
        var stopped = await ViewModel.StopForAppExitAsync();
        _stopBeforeCloseInProgress = false;

        if (stopped)
        {
            _allowClose = true;
            Close();
        }
    }
}
