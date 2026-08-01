using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Infrastructure;

public sealed class ServerStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(ColorHelper.FromArgb(255, 44, 203, 112));
    private static readonly SolidColorBrush TransitionBrush = new(ColorHelper.FromArgb(255, 245, 185, 66));
    private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 232, 72, 86));
    private static readonly SolidColorBrush StoppedBrush = new(ColorHelper.FromArgb(255, 138, 138, 138));

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ServerState.Running or ServerState.Ready => RunningBrush,
        ServerState.Starting or ServerState.Stopping or ServerState.LoadingProfile => TransitionBrush,
        ServerState.Failed or ServerState.InvalidProfile => ErrorBrush,
        _ => StoppedBrush
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
