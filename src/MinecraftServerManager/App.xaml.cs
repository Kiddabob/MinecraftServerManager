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
        services.AddSingleton<IProfileService, JsonProfileService>();
        services.AddSingleton<IProfileValidator, ProfileValidator>();
        services.AddSingleton<IServerFileService, ServerFileService>();
        services.AddSingleton<IServerLaunchRequestFactory, JavaServerLaunchRequestFactory>();
        services.AddSingleton<IServerConsoleParserFactory, ProfileConsoleParserFactory>();
        services.AddSingleton<IServerProcessService, ServerProcessService>();
        services.AddSingleton<IAppUpdateService, GitHubAppUpdateService>();
        services.AddSingleton<IUiDispatcher, DispatcherQueueUiDispatcher>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
