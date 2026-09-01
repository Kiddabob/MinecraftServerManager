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
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;
using MinecraftServerManager.ViewModels;
using Windows.ApplicationModel.DataTransfer;
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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinimumWindowWidth;
            presenter.PreferredMinimumHeight = MinimumWindowHeight;
        }

        AppWindow.Resize(new SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
        RestoreWindowPlacement();

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

    public void ShowWithRestoredPlacement()
    {
        var restoreMaximized = _restoreMaximizedOnLoaded;
        _restoreMaximizedOnLoaded = false;
        Activate();
        if (restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
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

    private void WorldMapScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            ViewModel.Map.SetZoomFactor(scrollViewer.ZoomFactor);
        }
    }

    private void UpdatePaneFooterVisibility()
    {
        var expandedVisibility = MainNavigationView.IsPaneOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        PaneHeaderContent.Visibility = expandedVisibility;
        ProfilePaneContext.Visibility = expandedVisibility;
        BrandTextStack.Visibility = expandedVisibility;
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
        MapPage.Visibility = destination == "map" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = destination == "files" ? Visibility.Visible : Visibility.Collapsed;
        ContentPage.Visibility = destination == "content" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = destination == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ModpacksPage.Visibility = destination == "modpacks" ? Visibility.Visible : Visibility.Collapsed;
        BuilderPage.Visibility = destination == "builder" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.Map.SetActive(destination == "map");

        if (destination == "files")
        {
            ViewModel.RefreshFilesCommand.Execute(null);
        }

        if (destination == "dashboard")
        {
            ViewModel.Dashboard.RefreshCommand.Execute(null);
        }

        if (destination == "content")
        {
            ViewModel.Content.EnsureLoaded();
        }

        if (destination == "console")
        {
            ScrollConsoleToLatest();
        }

        if (destination == "modpacks")
        {
            ViewModel.Modpacks.EnsureLoaded();
        }

        if (destination == "builder")
        {
            ViewModel.Builder.EnsureLoaded();
        }
    }

    private void BuilderSearchTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || !ViewModel.Builder.SearchCommand.CanExecute(null))
        {
            return;
        }

        args.Handled = true;
        ViewModel.Builder.SearchCommand.Execute(null);
    }

    private async void PrepareCurseForgeApplication_Click(object sender, RoutedEventArgs args)
    {
        var content = new StackPanel
        {
            Width = 500,
            Spacing = 14
        };
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Your application and your key",
            Message = "This helper describes a key for your own local installation. It does not collect your personal details, accept Overwolf's terms, submit the form, or imply approval. Review every answer before using it."
        });

        content.Children.Add(new TextBlock
        {
            Text = "Enter these personal fields directly in the official form",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = string.Join("  •  ", CurseForgeApplicationTemplate.PersonalFields),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var field in CurseForgeApplicationTemplate.SuggestedAnswers)
        {
            var copyButton = new Button
            {
                Content = "Copy answer",
                Tag = field.SuggestedAnswer,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            copyButton.Click += CopyCurseForgeApplicationText_Click;

            var heading = new Grid
            {
                ColumnSpacing = 10
            };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock
            {
                Text = field.FormArea,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(copyButton, 1);
            heading.Children.Add(copyButton);

            var fieldContent = new StackPanel { Spacing = 6 };
            fieldContent.Children.Add(heading);
            fieldContent.Children.Add(new TextBox
            {
                Text = field.SuggestedAnswer,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = field.SuggestedAnswer.Length > 360
                    ? 150
                    : field.SuggestedAnswer.Length > 150
                        ? 110
                        : 50
            });
            content.Children.Add(fieldContent);
        }

        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "You must make the declarations yourself",
            Message = "Read the current form, API terms, and privacy policy. Only tick statements you personally understand and accept. The suggested wording deliberately asks Overwolf to approve the per-installation key model and the minimal local audit manifest."
        });

        var copyAllButton = new Button
        {
            Content = "Copy full template",
            Tag = CurseForgeApplicationTemplate.CreatePlainText()
        };
        copyAllButton.Click += CopyCurseForgeApplicationText_Click;
        var guideButton = new Button
        {
            Content = "Read application guide",
            Tag = CurseForgeApplicationTemplate.ApplicationGuideUrl
        };
        guideButton.Click += OpenCurseForgeApplicationLink_Click;
        var formButton = new Button
        {
            Content = "Open official form",
            Tag = CurseForgeApplicationTemplate.ApplicationFormUrl
        };
        formButton.Click += OpenCurseForgeApplicationLink_Click;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(copyAllButton);
        actions.Children.Add(guideButton);
        actions.Children.Add(formButton);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Prepare your CurseForge application",
            Content = new ScrollViewer
            {
                MaxHeight = 590,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            MaxWidth = 760
        };
        await dialog.ShowAsync();
    }

    private static void CopyCurseForgeApplicationText_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string text } button)
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            button.Content = "Copied";
        }
        catch (Exception)
        {
            button.Content = "Copy failed";
        }
    }

    private static async void OpenCurseForgeApplicationLink_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string url })
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri(url));
    }

    private async void AddCurseForgeApiKey_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Builder.IsCurseForgeConnectionBusy)
        {
            return;
        }

        var passwordBox = new PasswordBox
        {
            Header = "Approved CurseForge developer API key",
            PlaceholderText = "Paste the key issued by CurseForge",
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxLength = 1024
        };
        var content = new StackPanel
        {
            Width = 520,
            Spacing = 12
        };
        content.Children.Add(new TextBlock
        {
            Text = "Only continue after CurseForge has approved your own developer application. The official application—not this dialog—records your acceptance of CurseForge's developer terms.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            FontSize = 12,
            Text = "The key is validated directly with CurseForge, then stored in Windows Credential Manager for this Windows account. It is never added to a server profile, installer, GitHub repository, or application log.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = ViewModel.Builder.IsCurseForgeApiKeyStored
                ? "Replace CurseForge developer key?"
                : "Connect CurseForge developer access",
            Content = content,
            PrimaryButtonText = "Validate and save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        passwordBox.PasswordChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(passwordBox.Password);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            passwordBox.Password = string.Empty;
            return;
        }

        var apiKey = passwordBox.Password;
        passwordBox.Password = string.Empty;
        await ViewModel.Builder.ConnectCurseForgeAsync(apiKey);
    }

    private async void RemoveCurseForgeApiKey_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Builder.IsCurseForgeApiKeyStored
            || ViewModel.Builder.IsCurseForgeConnectionBusy)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Disconnect CurseForge?",
            Content = "This removes the developer API key from Windows Credential Manager. Modrinth and other available sources will continue to work.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Builder.RemoveCurseForgeConnection();
        }
    }

    private void ModpackSearchTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || !ViewModel.Modpacks.SearchCommand.CanExecute(null))
        {
            return;
        }

        args.Handled = true;
        ViewModel.Modpacks.SearchCommand.Execute(null);
    }

    private async void ModpackPageList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is int pageNumber)
        {
            await ViewModel.Modpacks.GoToPageAsync(pageNumber);
        }
    }

    private async void BuilderSearchPageList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is int pageNumber)
        {
            await ViewModel.Builder.GoToSearchPageAsync(pageNumber);
        }
    }

    private void ServerContentSearchTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || !ViewModel.Content.SearchCommand.CanExecute(null))
        {
            return;
        }

        args.Handled = true;
        ViewModel.Content.SearchCommand.Execute(null);
    }

    private async void InstallServerContent_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Content.CanInstall)
        {
            return;
        }

        var plan = await ViewModel.Content.PrepareInstallAsync();
        if (plan is null || ViewModel.Content.IsServerActive)
        {
            return;
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            MaxWidth = 560,
            Text = $"{plan.SummaryText}. Every JAR is downloaded from Modrinth and verified with its published SHA-512 hash before it is moved into the server.",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var item in plan.Items)
        {
            content.Children.Add(new TextBlock
            {
                MaxWidth = 560,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 12,
                Text = $"• {item.DisplayName}\n  {item.DetailsText}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var warning in plan.Warnings)
        {
            content.Children.Add(new TextBlock
            {
                MaxWidth = 560,
                Foreground = new SolidColorBrush(Colors.Goldenrod),
                Text = $"Warning: {warning}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Install server content?",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            },
            PrimaryButtonText = "Install verified files",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || ViewModel.Content.IsServerActive)
        {
            return;
        }

        await ViewModel.Content.InstallAsync(plan);
    }

    private async void ReviewBuilderItem_Click(object sender, RoutedEventArgs args)
    {
        var plan = await ViewModel.Builder.PrepareAddAsync();
        if (plan is null)
        {
            return;
        }

        if (plan.IsReady && !plan.HasDependencyReview && plan.Warnings.Count == 0)
        {
            ViewModel.Builder.CommitPlan(plan);
            return;
        }

        var selectedOptionalDependencies = await ShowBuilderPlanDialogAsync(
            plan,
            plan.Conflicts.Count > 0 ? "Compatibility needs attention" : "Review dependencies",
            plan.IsReady ? "Add to draft" : string.Empty,
            includeOptionalChoices: true);
        if (selectedOptionalDependencies is null || !plan.IsReady)
        {
            return;
        }

        if (selectedOptionalDependencies.Count == 0)
        {
            ViewModel.Builder.CommitPlan(plan);
            return;
        }

        var finalPlan = await ViewModel.Builder.PrepareOptionalDependenciesAsync(
            plan,
            selectedOptionalDependencies);
        if (finalPlan is null)
        {
            return;
        }

        var confirmed = await ShowBuilderPlanDialogAsync(
            finalPlan,
            finalPlan.IsReady ? "Confirm draft additions" : "Dependency conflict",
            finalPlan.IsReady ? "Add reviewed items" : string.Empty,
            includeOptionalChoices: false);
        if (confirmed is not null && finalPlan.IsReady)
        {
            ViewModel.Builder.CommitPlan(finalPlan);
        }
    }

    private async Task<IReadOnlyList<PackOptionalDependencyChoice>?> ShowBuilderPlanDialogAsync(
        PackResolutionPlan plan,
        string title,
        string primaryButtonText,
        bool includeOptionalChoices)
    {
        var content = new StackPanel
        {
            Width = 640,
            Spacing = 16
        };
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = plan.Conflicts.Count > 0
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Informational,
            Title = plan.Conflicts.Count > 0 ? "Compatibility needs attention" : "Draft plan ready",
            Message = plan.SummaryText
        });

        content.Children.Add(CreateBuilderDialogSectionHeader(
            "Items in this change",
            $"{plan.Items.Count:N0} item{(plan.Items.Count == 1 ? string.Empty : "s")}"));
        foreach (var item in plan.Items)
        {
            content.Children.Add(CreateBuilderPlanItemCard(item));
        }

        var optionalCheckBoxes = new List<(CheckBox CheckBox, PackOptionalDependencyChoice Choice)>();
        if (includeOptionalChoices && plan.OptionalDependencies.Count > 0)
        {
            content.Children.Add(CreateBuilderDialogSectionHeader(
                "Optional dependencies",
                $"{plan.OptionalDependencies.Count:N0} available"));
            content.Children.Add(new TextBlock
            {
                MaxWidth = 640,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)),
                Text = "Choose only the extras you want. They start off, and any dependencies they require will be checked before the final confirmation.",
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var choice in plan.OptionalDependencies)
            {
                var card = CreateBuilderOptionalDependencyCard(choice, out var checkBox);
                optionalCheckBoxes.Add((checkBox, choice));
                content.Children.Add(card);
            }
        }

        foreach (var warning in plan.Warnings)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Review before adding",
                Message = warning
            });
        }

        foreach (var conflict in plan.Conflicts)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "Conflict",
                Message = conflict
            });
        }

        var draftNote = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(4, 0, 4, 0)
        };
        draftNote.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        draftNote.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        draftNote.Children.Add(new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
        });
        var draftNoteText = new TextBlock
        {
            Text = "Draft only — no files are downloaded, installed, or launched from this review.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150))
        };
        Grid.SetColumn(draftNoteText, 1);
        draftNote.Children.Add(draftNoteText);
        content.Children.Add(draftNote);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 620,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        // The WinUI template caps ContentDialog at 548 DIPs through theme
        // resources. Override those resources locally so the cards, badges,
        // and optional-dependency checkbox have room without clipping.
        dialog.Resources["ContentDialogMinWidth"] = 680d;
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(primaryButtonText))
        {
            return null;
        }

        return optionalCheckBoxes
            .Where(option => option.CheckBox.IsChecked == true)
            .Select(option => option.Choice)
            .ToArray();
    }

    private static Grid CreateBuilderDialogSectionHeader(string title, string countText)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var count = CreateBuilderBadge(
            countText,
            Color.FromArgb(255, 130, 130, 130));
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        return header;
    }

    private static Border CreateBuilderPlanItemCard(PackDraftItem item)
    {
        var stateText = item.DependencyType switch
        {
            "required" => "Required",
            "optional" => "Optional",
            _ => "Selected"
        };
        var stateColour = item.DependencyType switch
        {
            "required" => Color.FromArgb(255, 72, 180, 112),
            "optional" => Color.FromArgb(255, 218, 160, 45),
            _ => Color.FromArgb(255, 84, 150, 230)
        };
        var details = CreateBuilderDependencyDetails(
            item.DisplayName,
            item.VersionNumber,
            item.ProviderId,
            item.Kind,
            stateText,
            stateColour,
            item.PlacementText,
            item.Reason);
        return CreateBuilderDependencyCard(item.IconUrl, item.Kind, details, null);
    }

    private static Border CreateBuilderOptionalDependencyCard(
        PackOptionalDependencyChoice choice,
        out CheckBox checkBox)
    {
        checkBox = new CheckBox
        {
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ToolTipService.SetToolTip(checkBox, $"Include {choice.DisplayName} in this draft");
        var details = CreateBuilderDependencyDetails(
            choice.DisplayName,
            choice.Version.VersionNumber,
            choice.ProviderId,
            choice.Kind,
            "Optional",
            Color.FromArgb(255, 218, 160, 45),
            choice.Placement switch
            {
                PackContentPlacement.Client => "Client",
                PackContentPlacement.Server => "Server",
                PackContentPlacement.Both => "Client + server",
                _ => "Needs review"
            },
            $"Suggested by {choice.OwnerName}");
        return CreateBuilderDependencyCard(choice.IconUrl, choice.Kind, details, checkBox);
    }

    private static StackPanel CreateBuilderDependencyDetails(
        string displayName,
        string versionText,
        string providerId,
        ServerContentKind kind,
        string stateText,
        Color stateColour,
        string placementText,
        string reason)
    {
        var details = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        details.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(versionText)
            && !NormaliseBuilderLabel(versionText).Equals(
                NormaliseBuilderLabel(displayName),
                StringComparison.Ordinal))
        {
            details.Children.Add(new TextBlock
            {
                Text = versionText,
                FontSize = 12,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 155))
            });
        }

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        badges.Children.Add(CreateBuilderBadge(stateText, stateColour));
        badges.Children.Add(CreateBuilderBadge(
            placementText,
            Color.FromArgb(255, 115, 145, 200)));
        badges.Children.Add(CreateBuilderBadge(
            kind == ServerContentKind.Mod ? "Mod" : "Plugin",
            Color.FromArgb(255, 135, 135, 135)));
        badges.Children.Add(CreateBuilderBadge(
            ProviderDisplayName(providerId),
            ProviderColour(providerId)));
        details.Children.Add(badges);
        details.Children.Add(new TextBlock
        {
            Text = reason,
            FontSize = 12,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 155))
        });
        return details;
    }

    private static Border CreateBuilderDependencyCard(
        string iconUrl,
        ServerContentKind kind,
        FrameworkElement details,
        CheckBox? checkBox)
    {
        var layout = new Grid
        {
            ColumnSpacing = 12
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (checkBox is not null)
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        layout.Children.Add(CreateBuilderContentIcon(iconUrl, kind));
        Grid.SetColumn(details, 1);
        layout.Children.Add(details);
        if (checkBox is not null)
        {
            Grid.SetColumn(checkBox, 2);
            layout.Children.Add(checkBox);
        }

        return new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(42, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }

    private static Border CreateBuilderContentIcon(string iconUrl, ServerContentKind kind)
    {
        var iconLayer = new Grid();
        iconLayer.Children.Add(new FontIcon
        {
            Glyph = kind == ServerContentKind.Mod ? "\uE7FC" : "\uE943",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 155, 155)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri)
            && iconUri.Scheme == Uri.UriSchemeHttps)
        {
            iconLayer.Children.Add(new Image
            {
                Source = new BitmapImage(iconUri),
                Stretch = Stretch.UniformToFill
            });
        }

        return new Border
        {
            Width = 48,
            Height = 48,
            Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            CornerRadius = new CornerRadius(9),
            Child = iconLayer,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private static Border CreateBuilderBadge(string text, Color colour) => new()
    {
        Padding = new Thickness(7, 2, 7, 2),
        Background = new SolidColorBrush(Color.FromArgb(30, colour.R, colour.G, colour.B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, colour.R, colour.G, colour.B)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(colour)
        }
    };

    private static string ProviderDisplayName(string providerId) => providerId.Trim().ToLowerInvariant() switch
    {
        "curseforge" => "CurseForge",
        "modrinth" => "Modrinth",
        _ => providerId
    };

    private static Color ProviderColour(string providerId) => providerId.Trim().ToLowerInvariant() switch
    {
        "curseforge" => Color.FromArgb(255, 241, 100, 54),
        "modrinth" => Color.FromArgb(255, 27, 217, 106),
        _ => Color.FromArgb(255, 135, 135, 135)
    };

    private static string NormaliseBuilderLabel(string value) => new(
        value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private async void RemoveBuilderDraftItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { CommandParameter: PackDraftItem item })
        {
            await ViewModel.Builder.RemoveDraftItemAsync(item);
        }
    }

    private async void DownloadBuilderDraftToManaged_Click(object sender, RoutedEventArgs args)
    {
        var plan = await ViewModel.Builder.PrepareManagedOutputAsync();
        if (plan is not null)
        {
            await ConfirmAndCreateBuilderOutputAsync(plan);
        }
    }

    private async void DownloadBuilderDraftToFolder_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Builder.CanCreateOutput)
        {
            return;
        }

        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "Use this output folder"
        };
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var plan = await ViewModel.Builder.PrepareOutputAsync(folder.Path);
        if (plan is not null)
        {
            await ConfirmAndCreateBuilderOutputAsync(plan);
        }
    }

    private async Task ConfirmAndCreateBuilderOutputAsync(PackOutputPlan plan)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            MaxWidth = 600,
            Text = plan.SummaryText,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            MaxWidth = 600,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            Text = $"Output: {plan.DestinationDirectory}\nMinecraft: {plan.MinecraftVersion}\nTarget: {plan.Target}\nServer setup: {plan.ServerBaselineText}",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var item in plan.Items.Take(12))
        {
            content.Children.Add(new TextBlock
            {
                MaxWidth = 600,
                FontSize = 12,
                Text = $"• {item.DisplayName} ({item.ProviderId})\n  {item.DestinationText}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (plan.Items.Count > 12)
        {
            content.Children.Add(new TextBlock
            {
                MaxWidth = 600,
                FontSize = 12,
                Text = $"…and {plan.Items.Count - 12:N0} more verified files.",
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = plan.PreparesServerBaseline ? "Runnable server baseline" : "Content-only output",
            Message = plan.PreparesServerBaseline
                ? plan.Target == PackBuildTarget.ClientAndServer
                    ? "The exact official server loader will be installed and the server will be added as the selected manager profile. Afterward, you can approve adding the isolated Client folder to the manager-owned launcher. The normal Minecraft Launcher remains untouched. The server EULA remains unaccepted until you explicitly review it."
                    : "The exact official server loader will be installed in the atomic staging folder and the completed server will be added as a profile. Java must already be available. The Minecraft EULA remains unaccepted until you explicitly review it in the app."
                : plan.Target == PackBuildTarget.Client
                    ? "This creates a verified, isolated Client game directory. Afterward, you can approve installing the manager-owned portable launcher and adding the client as a separate instance. The normal Minecraft Launcher remains untouched."
                    : "This creates verified Client/Server mod and plugin folders plus a manifest. The selected platform does not yet have a safe runnable baseline installer here, and no Minecraft EULA is accepted."
        });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = plan.PreparesServerBaseline
                ? "Build the reviewed server pack?"
                : "Download the reviewed draft?",
            Content = new ScrollViewer
            {
                MaxHeight = 540,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            },
            PrimaryButtonText = plan.PreparesServerBaseline
                ? "Build server pack"
                : "Download verified files",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var result = await ViewModel.Builder.CreateOutputAsync(plan);
            if (result is null)
            {
                return;
            }

            if (result.ServerBaselinePrepared)
            {
                var importResult = await ViewModel.ImportServerFolderAsync(
                    result.ServerDirectory,
                    plan.PackName);
                var importedProfile = ViewModel.SelectedProfile;
                if (importedProfile is not null
                    && importedProfile.Id == importResult?.Profile?.Id)
                {
                    MainNavigationView.SelectedItem = OverviewNavigationItem;
                    if (importedProfile.Readiness.NeedsEulaAcceptance)
                    {
                        await ShowEulaAcceptanceDialogAsync(importedProfile, afterImport: true);
                    }
                }
            }

            if (plan.Target is PackBuildTarget.Client or PackBuildTarget.ClientAndServer)
            {
                await ConfirmAndAddClientToManagedLauncherAsync();
            }
        }
    }

    private async Task ConfirmAndAddClientToManagedLauncherAsync()
    {
        var isInstalled = ViewModel.Builder.IsManagedLauncherInstalled;
        var content = new StackPanel { MaxWidth = 590, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = isInstalled
                ? "This copies the built client into a new instance in the manager-owned portable launcher. It will not overwrite an existing instance or saved worlds."
                : $"This downloads the official Prism Launcher portable build into:\n{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kidda.MinecraftServerManager", "Launcher", "Prism")}\n\nIts accounts, instances, libraries, and settings stay in that folder. Your normal Minecraft Launcher and %APPDATA%\\.minecraft will not be read or changed.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Prism Launcher is a third-party, open-source launcher licensed under GPL-3.0-only. You sign in to Microsoft inside Prism Launcher; Minecraft Server Manager never receives your password or account tokens.",
            TextWrapping = TextWrapping.Wrap
        });
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        links.Children.Add(new HyperlinkButton
        {
            Content = "Prism Launcher website",
            NavigateUri = new Uri("https://prismlauncher.org/")
        });
        links.Children.Add(new HyperlinkButton
        {
            Content = "Source and licence",
            NavigateUri = new Uri("https://github.com/PrismLauncher/PrismLauncher")
        });
        content.Children.Add(links);

        var consentDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = isInstalled ? "Add client to managed launcher?" : "Install the managed launcher?",
            Content = content,
            PrimaryButtonText = isInstalled ? "Add client" : "Install and add client",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await consentDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var launcherResult = await ViewModel.Builder.RegisterLastClientWithManagedLauncherAsync();
        if (launcherResult is not null)
        {
            await ShowManagedLauncherReadyDialogAsync(launcherResult);
        }
    }

    private async Task ShowManagedLauncherReadyDialogAsync(
        ManagedLauncherInstallResult launcherResult)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Client pack ready",
            Content = new TextBlock
            {
                MaxWidth = 560,
                Text = $"{launcherResult.Message}\n\nThe managed launcher will download Minecraft's normal game libraries the first time you press Play. Sign in inside Prism Launcher when prompted; the manager does not read or change your Microsoft credentials.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Open managed launcher",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Builder.TryOpenManagedLauncher();
        }
    }

    private async void RegisterBuilderClientWithManagedLauncher_Click(object sender, RoutedEventArgs args)
    {
        await ConfirmAndAddClientToManagedLauncherAsync();
    }

    private void OpenManagedLauncher_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.Builder.TryOpenManagedLauncher();
    }

    private async void OpenBuilderOutputFolder_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Builder.HasLastOutput)
        {
            return;
        }

        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
                ViewModel.Builder.LastOutputDirectory);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or ArgumentException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Output folder could not be opened",
                Content = exception.Message,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
    }

    private async void ImportModpackToManagedInstances_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Modpacks.CanImport)
        {
            return;
        }

        await ImportModpackAndOpenProfileAsync(
            ViewModel.Modpacks.ImportToManagedInstancesAsync);
    }

    private async void ImportModpackToCustomFolder_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.Modpacks.CanImport)
        {
            return;
        }

        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "Create server folder here"
        };

        var location = await picker.PickSingleFolderAsync();
        if (location is null)
        {
            return;
        }

        await ImportModpackAndOpenProfileAsync(
            () => ViewModel.Modpacks.ImportAsync(location.Path));
    }

    private async Task ImportModpackAndOpenProfileAsync(
        Func<Task<ModpackImportResult?>> import)
    {
        try
        {
            var result = await import();
            if (result is not null)
            {
                await ViewModel.AcceptProfileImportAsync(result.ProfileImport);
                var importedProfile = ViewModel.SelectedProfile;
                if (importedProfile is not null
                    && importedProfile.Id == result.ProfileImport.Profile?.Id
                    && importedProfile.Readiness.NeedsEulaAcceptance)
                {
                    await ShowEulaAcceptanceDialogAsync(importedProfile, afterImport: true);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "The server pack was installed",
                Content = $"Its profile was saved, but the app could not open it immediately: {exception.Message}\n\nRestarting the app will load the saved profile.",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
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

    private async void DuplicateServerProfile_Click(object sender, RoutedEventArgs args)
    {
        var selected = ViewModel.SelectedProfile;
        if (selected is null || selected.IsServerActive)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = "New profile name",
            Text = $"{selected.DisplayName} copy"
        };
        var includeWorlds = new CheckBox
        {
            Content = "Include worlds and player data",
            IsChecked = false
        };
        var validationText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.OrangeRed),
            Text = "Enter a name for the copied profile.",
            Visibility = Visibility.Collapsed
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            MaxWidth = 560,
            Text = "The original server is never changed. The copy keeps mods, plugins, configuration, and launch settings; logs, crash reports, backups, caches, and the previous EULA acceptance are left out.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(nameBox);
        content.Children.Add(includeWorlds);
        content.Children.Add(new TextBlock
        {
            MaxWidth = 560,
            Style = (Style)Application.Current.Resources["BodySecondaryTextStyle"],
            Text = "Leave this unticked for a clean editable copy. Tick it to clone the current worlds, inventories, and player data too.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new Border
        {
            Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 11,
                Text = ViewModel.ManagedInstancesDirectory,
                TextWrapping = TextWrapping.Wrap
            }
        });
        content.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Duplicate {selected.DisplayName}",
            Content = content,
            PrimaryButtonText = "Copy to Instances",
            SecondaryButtonText = "Choose location",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        void ValidateName(ContentDialog _, ContentDialogButtonClickEventArgs clickArgs)
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text))
            {
                return;
            }

            validationText.Visibility = Visibility.Visible;
            clickArgs.Cancel = true;
        }

        dialog.PrimaryButtonClick += ValidateName;
        dialog.SecondaryButtonClick += ValidateName;
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return;
        }

        ServerProfileDuplicateResult? result;
        if (choice == ContentDialogResult.Primary)
        {
            result = await ViewModel.DuplicateSelectedProfileToManagedInstancesAsync(
                nameBox.Text,
                includeWorlds.IsChecked == true);
        }
        else
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
                CommitButtonText = "Create copied server here"
            };
            var destination = await picker.PickSingleFolderAsync();
            if (destination is null)
            {
                return;
            }

            result = await ViewModel.DuplicateSelectedProfileAsync(
                nameBox.Text,
                destination.Path,
                includeWorlds.IsChecked == true);
        }

        var copiedProfile = ViewModel.SelectedProfile;
        if (result?.ProfileImport.Profile is not null
            && copiedProfile?.Id == result.ProfileImport.Profile.Id
            && copiedProfile.Readiness.NeedsEulaAcceptance)
        {
            await ShowEulaAcceptanceDialogAsync(copiedProfile, afterImport: true);
        }
    }

    private async void AcceptEula_Click(object sender, RoutedEventArgs args)
    {
        var profile = ViewModel.SelectedProfile;
        if (profile?.Readiness.NeedsEulaAcceptance != true)
        {
            return;
        }

        await ShowEulaAcceptanceDialogAsync(profile, afterImport: false);
    }

    private async Task<bool> ShowEulaAcceptanceDialogAsync(
        ServerSessionViewModel profile,
        bool afterImport)
    {
        var agreementCheckBox = new CheckBox
        {
            Content = new TextBlock
            {
                MaxWidth = 520,
                Text = "I have read and agree to the Minecraft End User License Agreement.",
                TextWrapping = TextWrapping.Wrap
            }
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            MaxWidth = 540,
            Text = afterImport
                ? $"{profile.DisplayName} is installed. Before its first full start, review the official Minecraft EULA. The manager changes eula.txt only after you confirm below."
                : $"Review the official Minecraft EULA before enabling {profile.DisplayName}. The manager changes eula.txt only after you confirm below.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new HyperlinkButton
        {
            Content = "Read the official Minecraft EULA",
            NavigateUri = new Uri("https://www.minecraft.net/en-us/eula")
        });
        content.Children.Add(agreementCheckBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = afterImport ? "Finish server setup" : "Accept the Minecraft EULA?",
            Content = content,
            PrimaryButtonText = "Accept and enable server",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        agreementCheckBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        agreementCheckBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        var accepted = await profile.AcceptEulaAsync();
        if (accepted)
        {
            return true;
        }

        var failureDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "The EULA setting could not be saved",
            Content = profile.StatusMessage,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };
        await failureDialog.ShowAsync();
        return false;
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

    private void ThemeChoice_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        var option = ViewModel.ThemeOptions.FirstOrDefault(candidate => candidate.Id == id);
        if (option is null)
        {
            return;
        }

        ViewModel.SelectedThemeOption = option;
        ApplyAppearance();
    }

    private void AccentChoice_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        if (id == "Custom" && !TryParseColor(ViewModel.CustomAccentHex, out _))
        {
            CustomAccentValidationMessage.Visibility = Visibility.Visible;
            CustomAccentHexTextBox.Focus(FocusState.Programmatic);
            return;
        }

        var option = ViewModel.AccentOptions.FirstOrDefault(candidate => candidate.Id == id);
        if (option is null)
        {
            return;
        }

        CustomAccentValidationMessage.Visibility = Visibility.Collapsed;
        ViewModel.SelectedAccentOption = option;
        ApplyAppearance();
    }

    private void ApplyCustomAccent_Click(object sender, RoutedEventArgs args)
    {
        var candidate = CustomAccentHexTextBox.Text.Trim();
        if (!candidate.StartsWith('#'))
        {
            candidate = $"#{candidate}";
        }

        if (!TryParseColor(candidate, out _))
        {
            CustomAccentValidationMessage.Visibility = Visibility.Visible;
            CustomAccentHexTextBox.Focus(FocusState.Programmatic);
            return;
        }

        ViewModel.CustomAccentHex = candidate.ToUpperInvariant();
        ViewModel.SelectedAccentOption = ViewModel.AccentOptions.First(option => option.Id == "Custom");
        CustomAccentValidationMessage.Visibility = Visibility.Collapsed;
        ApplyAppearance();
    }

    private async void OpenExternalUri_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string value }
            && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
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

        SystemBackdrop = ViewModel.SelectedBackdropOption?.Id == "Acrylic"
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop();

        var accentColor = ViewModel.SelectedAccentOption?.Id switch
        {
            "System" => GetSystemAccentColor(),
            "Custom" => ParseColor(ViewModel.CustomAccentHex),
            _ => ParseColor(ViewModel.SelectedAccentOption?.HexColor ?? "#60CDFF")
        };

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

        UpdateAppearanceChoiceVisuals(accentColor);
    }

    private void UpdateAppearanceChoiceVisuals(Color accentColor)
    {
        var selectedBrush = new SolidColorBrush(accentColor);
        var clearBrush = new SolidColorBrush(Colors.Transparent);
        var selectedTheme = ViewModel.SelectedThemeOption?.Id ?? "System";
        foreach (var (id, card) in new (string Id, Border Card)[]
        {
            ("System", SystemThemeCard),
            ("Light", LightThemeCard),
            ("Dark", DarkThemeCard)
        })
        {
            card.BorderBrush = id == selectedTheme ? selectedBrush : clearBrush;
        }

        var selectedAccent = ViewModel.SelectedAccentOption?.Id ?? "System";
        foreach (var (id, card) in new (string Id, Border Card)[]
        {
            ("System", SystemAccentCard),
            ("Coral", CoralAccentCard),
            ("Blue", BlueAccentCard),
            ("Emerald", EmeraldAccentCard),
            ("Amethyst", AmethystAccentCard),
            ("Amber", AmberAccentCard),
            ("Custom", CustomAccentCard)
        })
        {
            card.BorderBrush = id == selectedAccent ? selectedBrush : clearBrush;
        }

        SelectedAccentPreview.Fill = selectedBrush;
        SelectedAccentName.Text = ViewModel.SelectedAccentOption?.DisplayName ?? "Windows accent";
        SystemAccentSwatch.Fill = new SolidColorBrush(GetSystemAccentColor());
        CustomAccentSwatch.Fill = new SolidColorBrush(ParseColor(ViewModel.CustomAccentHex));
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
        return TryParseColor(value, out var color)
            ? color
            : Colors.DeepSkyBlue;
    }

    private static bool TryParseColor(string value, out Color color)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6
            || !uint.TryParse(
                hex,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rgb))
        {
            color = Colors.Transparent;
            return false;
        }

        color = Color.FromArgb(
            255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        return true;
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

    private async Task PersistWindowPlacementAsync(bool? isMaximizedOverride = null)
    {
        var isMaximized = isMaximizedOverride
            ?? (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Maximized);
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

        var wasMaximizedBeforeCancellingClose = sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized;
        args.Cancel = true;
        if (_stopBeforeCloseInProgress)
        {
            return;
        }

        _stopBeforeCloseInProgress = true;
        _windowPlacementSaveTimer.Stop();
        await PersistWindowPlacementAsync(wasMaximizedBeforeCancellingClose);
        var stopped = await ViewModel.StopForAppExitAsync();
        _stopBeforeCloseInProgress = false;

        if (stopped)
        {
            _allowClose = true;
            Close();
        }
    }
}
