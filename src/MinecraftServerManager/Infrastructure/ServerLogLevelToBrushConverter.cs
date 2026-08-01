using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Infrastructure;

public sealed class ServerLogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InformationBrush = new(ColorHelper.FromArgb(255, 96, 205, 255));
    private static readonly SolidColorBrush WarningBrush = new(ColorHelper.FromArgb(255, 245, 185, 66));
    private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 232, 72, 86));
    private static readonly SolidColorBrush SuccessBrush = new(ColorHelper.FromArgb(255, 44, 203, 112));
    private static readonly SolidColorBrush CommandBrush = new(ColorHelper.FromArgb(255, 167, 139, 250));
    private static readonly SolidColorBrush ManagerBrush = new(ColorHelper.FromArgb(255, 160, 160, 160));

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ServerLogLevel.Warning => WarningBrush,
        ServerLogLevel.Error => ErrorBrush,
        ServerLogLevel.Success => SuccessBrush,
        ServerLogLevel.Command => CommandBrush,
        ServerLogLevel.Manager => ManagerBrush,
        _ => InformationBrush
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
