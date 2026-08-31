using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Infrastructure;

public sealed class AppUpdateStateToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        AppUpdateState.UpToDate or AppUpdateState.ReadyToApply => InfoBarSeverity.Success,
        AppUpdateState.Failed => InfoBarSeverity.Error,
        AppUpdateState.Disabled => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
