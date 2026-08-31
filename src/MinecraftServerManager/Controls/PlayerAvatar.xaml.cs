using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MinecraftServerManager.Controls;

public sealed partial class PlayerAvatar : UserControl
{
    public static readonly DependencyProperty AvatarPathProperty = DependencyProperty.Register(
        nameof(AvatarPath),
        typeof(string),
        typeof(PlayerAvatar),
        new PropertyMetadata(null, OnAvatarPathChanged));

    public static readonly DependencyProperty PlayerNameProperty = DependencyProperty.Register(
        nameof(PlayerName),
        typeof(string),
        typeof(PlayerAvatar),
        new PropertyMetadata(null, OnPlayerNameChanged));

    private int _loadVersion;
    private CompositionSurfaceBrush? _imageBrush;
    private SpriteVisual? _imageVisual;
    private LoadedImageSurface? _skinSurface;
    private IRandomAccessStream? _skinStream;

    public PlayerAvatar()
    {
        InitializeComponent();
        Loaded += PlayerAvatar_Loaded;
        Unloaded += PlayerAvatar_Unloaded;
    }

    public string? AvatarPath
    {
        get => (string?)GetValue(AvatarPathProperty);
        set => SetValue(AvatarPathProperty, value);
    }

    public string? PlayerName
    {
        get => (string?)GetValue(PlayerNameProperty);
        set => SetValue(PlayerNameProperty, value);
    }

    private static void OnAvatarPathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (PlayerAvatar)dependencyObject;
        if (control.IsLoaded)
        {
            _ = control.LoadSkinAsync(args.NewValue as string);
        }
        else
        {
            control.ShowFallback();
        }
    }

    private static void OnPlayerNameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (PlayerAvatar)dependencyObject;
        var name = args.NewValue as string;
        control.FallbackInitial.Text = string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim()[..1].ToUpperInvariant();
    }

    private async Task LoadSkinAsync(string? avatarPath)
    {
        var loadVersion = ++_loadVersion;
        ShowFallback();
        if (string.IsNullOrWhiteSpace(avatarPath) || !File.Exists(avatarPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(avatarPath);
            using var sourceStream = await file.OpenReadAsync();
            var stream = await CreateAvatarStreamAsync(sourceStream);
            if (stream is null)
            {
                return;
            }

            if (loadVersion != _loadVersion || !IsLoaded)
            {
                stream.Dispose();
                return;
            }

            EnsureCompositionVisuals();
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            _skinSurface = surface;
            _skinStream = stream;
            _imageBrush!.Surface = surface;
            surface.LoadCompleted += (_, args) =>
            {
                if (loadVersion != _loadVersion || !ReferenceEquals(surface, _skinSurface))
                {
                    surface.Dispose();
                    stream.Dispose();
                    return;
                }

                _skinStream?.Dispose();
                _skinStream = null;
                if (args.Status == LoadedImageSourceLoadStatus.Success)
                {
                    SkinBorder.Visibility = Visibility.Visible;
                    FallbackBorder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowFallback();
                }
            };
        }
        catch
        {
            if (loadVersion == _loadVersion)
            {
                ShowFallback();
            }
        }
    }

    private void ShowFallback()
    {
        ReleaseSkinSurface();
        SkinBorder.Visibility = Visibility.Collapsed;
        FallbackBorder.Visibility = Visibility.Visible;
    }

    private void PlayerAvatar_Loaded(object sender, RoutedEventArgs args)
    {
        EnsureCompositionVisuals();
        _ = LoadSkinAsync(AvatarPath);
    }

    private void PlayerAvatar_Unloaded(object sender, RoutedEventArgs args)
    {
        _loadVersion++;
        ReleaseSkinSurface();
        ReleaseCompositionVisuals();
    }

    private void EnsureCompositionVisuals()
    {
        if (_imageBrush is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(ImageHost).Compositor;
        _imageBrush = PixelArtRendering.CreateSurfaceBrush(compositor, CompositionStretch.Fill);
        _imageVisual = compositor.CreateSpriteVisual();
        _imageVisual.Brush = _imageBrush;
        _imageVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(ImageHost, _imageVisual);
    }

    private void ReleaseSkinSurface()
    {
        if (_imageBrush is not null)
        {
            _imageBrush.Surface = null;
        }

        _skinSurface?.Dispose();
        _skinSurface = null;
        _skinStream?.Dispose();
        _skinStream = null;
    }

    private void ReleaseCompositionVisuals()
    {
        ElementCompositionPreview.SetElementChildVisual(ImageHost, null);
        _imageVisual?.Dispose();
        _imageVisual = null;
        _imageBrush?.Dispose();
        _imageBrush = null;
    }

    private static async Task<InMemoryRandomAccessStream?> CreateAvatarStreamAsync(
        IRandomAccessStream sourceStream)
    {
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        if (decoder.PixelWidth < 48 || decoder.PixelHeight < 16)
        {
            return null;
        }

        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var skinPixels = pixelProvider.DetachPixelData();
        var skinWidth = checked((int)decoder.PixelWidth);
        var avatarPixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var destinationOffset = ((y * 8) + x) * 4;
                var faceOffset = ((((y + 8) * skinWidth) + x + 8) * 4);
                var hatOffset = ((((y + 8) * skinWidth) + x + 40) * 4);
                CompositePixel(skinPixels, faceOffset, hatOffset, avatarPixels, destinationOffset);
            }
        }

        var avatarStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, avatarStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            8,
            8,
            96,
            96,
            avatarPixels);
        await encoder.FlushAsync();
        avatarStream.Seek(0);
        return avatarStream;
    }

    private static void CompositePixel(
        IReadOnlyList<byte> skinPixels,
        int faceOffset,
        int hatOffset,
        IList<byte> destination,
        int destinationOffset)
    {
        var faceAlpha = skinPixels[faceOffset + 3];
        var hatAlpha = skinPixels[hatOffset + 3];
        var inverseHatAlpha = 255 - hatAlpha;
        var outputAlpha = hatAlpha + ((faceAlpha * inverseHatAlpha + 127) / 255);
        for (var channel = 0; channel < 3; channel++)
        {
            if (outputAlpha == 0)
            {
                destination[destinationOffset + channel] = 0;
                continue;
            }

            var premultiplied = (skinPixels[hatOffset + channel] * hatAlpha)
                + ((skinPixels[faceOffset + channel] * faceAlpha * inverseHatAlpha + 127) / 255);
            destination[destinationOffset + channel] =
                (byte)Math.Clamp((premultiplied + (outputAlpha / 2)) / outputAlpha, 0, 255);
        }

        destination[destinationOffset + 3] = (byte)outputAlpha;
    }
}
