using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Services;
using MinecraftServerManager.ViewModels;
using MinecraftServerManager.Views;

namespace MinecraftServerManager;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    public IServiceProvider Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.ShowWithRestoredPlacement();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IAppSettingsService, JsonAppSettingsService>();
        services.AddSingleton<IWindowPlacementService, JsonWindowPlacementService>();
        services.AddSingleton<IPlayerPlaytimeService, JsonPlayerPlaytimeService>();
        services.AddSingleton<IPlayerAvatarService, MojangPlayerAvatarService>();
        services.AddSingleton<IWorldMapService, LegacyAnvilWorldMapService>();
        services.AddSingleton<IJavaRuntimeService, JavaRuntimeService>();
        services.AddSingleton<IServerLaunchRecommendationService, ServerLaunchRecommendationService>();
        services.AddSingleton<IManagedJavaRuntimeService, ManagedJavaRuntimeService>();
        services.AddSingleton<ModrinthModpackCatalogService>();
        services.AddSingleton<TechnicModpackCatalogService>();
        services.AddSingleton<FtbModpackCatalogService>();
        services.AddSingleton<IModpackCatalogProvider>(provider =>
            provider.GetRequiredService<ModrinthModpackCatalogService>());
        services.AddSingleton<IModpackCatalogProvider>(provider =>
            provider.GetRequiredService<TechnicModpackCatalogService>());
        services.AddSingleton<IModpackCatalogProvider>(provider =>
            provider.GetRequiredService<FtbModpackCatalogService>());
        services.AddSingleton<IModpackCatalogService, ModpackCatalogService>();
        services.AddSingleton<IServerContentInventoryService, ServerContentInventoryService>();
        services.AddSingleton<ModrinthServerContentCatalogService>();
        services.AddSingleton<IServerContentCatalogService>(provider =>
            provider.GetRequiredService<ModrinthServerContentCatalogService>());
        services.AddSingleton<IPackContentCatalogProvider>(provider =>
            provider.GetRequiredService<ModrinthServerContentCatalogService>());
        services.AddSingleton<ICurseForgeApiKeyStore, WindowsCredentialManagerApiKeyStore>();
        services.AddSingleton<ICurseForgeApiKeyService, CurseForgeApiKeyService>();
        services.AddSingleton<IPackContentCatalogProvider, CurseForgePackContentCatalogProvider>();
        services.AddSingleton<IPackContentCatalogService, PackContentCatalogService>();
        services.AddSingleton<IPackContentDownloadProvider, ModrinthPackContentDownloadProvider>();
        services.AddSingleton<IPackContentDownloadProvider, CurseForgePackContentDownloadProvider>();
        services.AddSingleton<IPackDraftOutputService, PackDraftOutputService>();
        services.AddSingleton<IPackDependencyResolver, PackDependencyResolver>();
        services.AddSingleton<IPackPlatformCatalogService, PackPlatformCatalogService>();
        services.AddSingleton<IPackPlatformVersionService, PackPlatformVersionService>();
        services.AddSingleton<IServerContentInstallService, ServerContentInstallService>();
        services.AddSingleton<IModpackInstallLocationService, ModpackInstallLocationService>();
        services.AddSingleton<IServerBaselineInstaller, VanillaServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, FabricServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, ForgeServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, NeoForgeServerBaselineInstaller>();
        services.AddSingleton<IServerBaselineInstaller, QuiltServerBaselineInstaller>();
        services.AddSingleton<IProfileService, JsonProfileService>();
        services.AddSingleton<ModrinthModpackImportService>();
        services.AddSingleton<TechnicModpackImportService>();
        services.AddSingleton<FtbModpackImportService>();
        services.AddSingleton<IModpackImportProvider>(provider =>
            provider.GetRequiredService<ModrinthModpackImportService>());
        services.AddSingleton<IModpackImportProvider>(provider =>
            provider.GetRequiredService<TechnicModpackImportService>());
        services.AddSingleton<IModpackImportProvider>(provider =>
            provider.GetRequiredService<FtbModpackImportService>());
        services.AddSingleton<IModpackImportService, ModpackImportService>();
        services.AddSingleton<IServerProfileDuplicateService, ServerProfileDuplicateService>();
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
        services.AddSingleton<ServerMapViewModel>();
        services.AddSingleton<ServerContentViewModel>();
        services.AddSingleton<ModpackCatalogViewModel>();
        services.AddSingleton<PackBuilderViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
