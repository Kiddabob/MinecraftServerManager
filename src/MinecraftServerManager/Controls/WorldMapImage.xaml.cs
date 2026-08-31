using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace MinecraftServerManager.Controls;

public sealed partial class WorldMapImage : UserControl
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath),
        typeof(string),
        typeof(WorldMapImage),
        new PropertyMetadata(null, OnImagePathChanged));

    private int _loadVersion;

    public WorldMapImage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _loadVersion++;
    }

    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    private static void OnImagePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = ((WorldMapImage)dependencyObject).LoadImageAsync(args.NewValue as string);
    }

    private async Task LoadImageAsync(string? imagePath)
    {
        var loadVersion = ++_loadVersion;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            MapImage.Source = null;
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (loadVersion == _loadVersion)
            {
                MapImage.Source = bitmap;
            }
        }
        catch
        {
            if (loadVersion == _loadVersion)
            {
                MapImage.Source = null;
            }
        }
    }
}
