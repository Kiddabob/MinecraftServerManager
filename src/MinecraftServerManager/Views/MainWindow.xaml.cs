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
            ApplyAppearance();
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

        if (Application.Current.Resources["AppAccentBrush"] is not SolidColorBrush accentBrush)
        {
            return;
        }

        accentBrush.Color = ViewModel.SelectedAccentOption?.Id == "System"
            ? GetSystemAccentColor()
            : ParseColor(ViewModel.SelectedAccentOption?.HexColor ?? "#60CDFF");
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

    private void ConsoleTextBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        ConsoleTextBox.SelectionStart = ConsoleTextBox.Text.Length;
        ConsoleTextBox.SelectionLength = 0;
    }

    private void CommandTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || !ViewModel.SendCommand.CanExecute(null))
        {
            return;
        }

        args.Handled = true;
        ViewModel.SendCommand.Execute(null);
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
