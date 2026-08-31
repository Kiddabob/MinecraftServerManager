using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MinecraftServerManager.Controls;

public sealed partial class WorldMapImage : UserControl
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath),
        typeof(string),
        typeof(WorldMapImage),
        new PropertyMetadata(null, OnImagePathChanged));

    private int _loadVersion;
    private CompositionSurfaceBrush? _imageBrush;
    private SpriteVisual? _imageVisual;
    private LoadedImageSurface? _imageSurface;
    private IRandomAccessStream? _imageStream;

    public WorldMapImage()
    {
        InitializeComponent();
        Loaded += WorldMapImage_Loaded;
        SizeChanged += (_, _) => UpdateVisualSize();
        Unloaded += WorldMapImage_Unloaded;
    }

    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    private static void OnImagePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (WorldMapImage)dependencyObject;
        if (control.IsLoaded)
        {
            _ = control.LoadImageAsync(args.NewValue as string);
        }
    }

    private async Task LoadImageAsync(string? imagePath)
    {
        var loadVersion = ++_loadVersion;
        ReleaseImageSurface();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            var stream = await file.OpenReadAsync();
            if (loadVersion != _loadVersion || !IsLoaded)
            {
                stream.Dispose();
                return;
            }

            EnsureCompositionVisual();
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            _imageSurface = surface;
            _imageStream = stream;
            _imageBrush!.Surface = surface;
            surface.LoadCompleted += (_, args) =>
            {
                if (loadVersion != _loadVersion || !ReferenceEquals(surface, _imageSurface))
                {
                    surface.Dispose();
                    stream.Dispose();
                    return;
                }

                _imageStream?.Dispose();
                _imageStream = null;
                if (args.Status != LoadedImageSourceLoadStatus.Success)
                {
                    ReleaseImageSurface();
                }
            };
        }
        catch
        {
            if (loadVersion == _loadVersion)
            {
                ReleaseImageSurface();
            }
        }
    }

    private void WorldMapImage_Loaded(object sender, RoutedEventArgs args)
    {
        EnsureCompositionVisual();
        UpdateVisualSize();
        _ = LoadImageAsync(ImagePath);
    }

    private void WorldMapImage_Unloaded(object sender, RoutedEventArgs args)
    {
        _loadVersion++;
        ReleaseImageSurface();
        ElementCompositionPreview.SetElementChildVisual(ImageHost, null);
        _imageVisual?.Dispose();
        _imageVisual = null;
        _imageBrush?.Dispose();
        _imageBrush = null;
    }

    private void EnsureCompositionVisual()
    {
        if (_imageBrush is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(ImageHost).Compositor;
        _imageBrush = PixelArtRendering.CreateSurfaceBrush(compositor, CompositionStretch.Fill);
        _imageVisual = compositor.CreateSpriteVisual();
        _imageVisual.Brush = _imageBrush;
        ElementCompositionPreview.SetElementChildVisual(ImageHost, _imageVisual);
    }

    private void UpdateVisualSize()
    {
        if (_imageVisual is null || ImageHost.ActualWidth <= 0 || ImageHost.ActualHeight <= 0)
        {
            return;
        }

        _imageVisual.Size = new Vector2((float)ImageHost.ActualWidth, (float)ImageHost.ActualHeight);
    }

    private void ReleaseImageSurface()
    {
        if (_imageBrush is not null)
        {
            _imageBrush.Surface = null;
        }

        _imageSurface?.Dispose();
        _imageSurface = null;
        _imageStream?.Dispose();
        _imageStream = null;
    }
}
