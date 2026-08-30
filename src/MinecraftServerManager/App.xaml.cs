using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Services;
using MinecraftServerManager.ViewModels;
using MinecraftServerManager.Views;

namespace MinecraftServerManager;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    public IServiceProvider Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IAppSettingsService, JsonAppSettingsService>();
        services.AddSingleton<IWindowPlacementService, JsonWindowPlacementService>();
        services.AddSingleton<IPlayerPlaytimeService, JsonPlayerPlaytimeService>();
        services.AddSingleton<IJavaRuntimeService, JavaRuntimeService>();
        services.AddSingleton<IServerLaunchRecommendationService, ServerLaunchRecommendationService>();
        services.AddSingleton<IManagedJavaRuntimeService, ManagedJavaRuntimeService>();
        services.AddSingleton<IModpackCatalogService, ModrinthModpackCatalogService>();
        services.AddSingleton<IModpackInstallLocationService, ModpackInstallLocationService>();
        services.AddSingleton<IServerBaselineInstaller, VanillaServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, FabricServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, ForgeServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, NeoForgeServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, QuiltServerBaselineInstaller>();
        services.AddSingleton<IProfileService, JsonProfileService>();
        services.AddSingleton<IModpackImportService, ModrinthModpackImportService>();
        services.AddSingleton<IProfileValidator, ProfileValidator>();
        services.AddSingleton<IServerReadinessService, ServerReadinessService>();
        services.AddSingleton<IServerFileService, ServerFileService>();
        services.AddSingleton<IServerConfigurationService, ServerConfigurationService>();
        services.AddSingleton<IServerConfigurationEditorService, ServerConfigurationEditorService>();
        services.AddSingleton<IServerLaunchRequestFactory, JavaServerLaunchRequestFactory>();
        services.AddSingleton<IServerConsoleParserFactory, ProfileConsoleParserFactory>();
        services.AddSingleton<IServerProcessServiceFactory, ServerProcessServiceFactory>();
        services.AddSingleton<IAppUpdateService, GitHubAppUpdateService>();
        services.AddSingleton<IUiDispatcher, DispatcherQueueUiDispatcher>();
        services.AddSingleton<ServerDashboardViewModel>();
        services.AddSingleton<ModpackCatalogViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
