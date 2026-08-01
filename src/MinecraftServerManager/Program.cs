using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace MinecraftServerManager;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
