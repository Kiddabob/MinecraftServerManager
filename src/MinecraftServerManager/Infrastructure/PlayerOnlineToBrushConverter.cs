using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MinecraftServerManager.Infrastructure;

public sealed class PlayerOnlineToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OnlineBrush = new(Color.FromArgb(255, 44, 203, 112));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromArgb(255, 157, 167, 179));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? OnlineBrush : OfflineBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
