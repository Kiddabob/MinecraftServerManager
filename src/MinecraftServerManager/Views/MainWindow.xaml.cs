using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MinecraftServerManager.ViewModels;
using Windows.System;

namespace MinecraftServerManager.Views;

public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private bool _allowClose;
    private bool _stopBeforeCloseInProgress;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        RootGrid.DataContext = ViewModel;
        Title = "Minecraft Server Manager";
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
