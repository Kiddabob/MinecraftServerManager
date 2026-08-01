using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace MinecraftServerManager.Controls;

public sealed partial class ProfileIcon : UserControl
{
    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath),
        typeof(string),
        typeof(ProfileIcon),
        new PropertyMetadata(null, OnIconPathChanged));

    public static readonly DependencyProperty StateBrushProperty = DependencyProperty.Register(
        nameof(StateBrush),
        typeof(Brush),
        typeof(ProfileIcon),
        new PropertyMetadata(null, OnStateBrushChanged));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph),
        typeof(string),
        typeof(ProfileIcon),
        new PropertyMetadata("\uE8D4", OnFallbackGlyphChanged));

    private int _loadVersion;

    public ProfileIcon()
    {
        InitializeComponent();
        Unloaded += (_, _) => _loadVersion++;
    }

    public string? IconPath
    {
        get => (string?)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public Brush? StateBrush
    {
        get => (Brush?)GetValue(StateBrushProperty);
        set => SetValue(StateBrushProperty, value);
    }

    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }

    private static void OnIconPathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = ((ProfileIcon)dependencyObject).LoadIconAsync(args.NewValue as string);
    }

    private static void OnStateBrushChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is Brush brush)
        {
            ((ProfileIcon)dependencyObject).FallbackBorder.Background = brush;
        }
    }

    private static void OnFallbackGlyphChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((ProfileIcon)dependencyObject).FallbackIcon.Glyph = args.NewValue as string ?? "\uE8D4";
    }

    private async Task LoadIconAsync(string? iconPath)
    {
        var loadVersion = ++_loadVersion;
        ShowFallback();

        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(iconPath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            if (loadVersion != _loadVersion)
            {
                return;
            }

            IconImage.Source = bitmap;
            ImageBorder.Visibility = Visibility.Visible;
            FallbackBorder.Visibility = Visibility.Collapsed;
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
        IconImage.Source = null;
        ImageBorder.Visibility = Visibility.Collapsed;
        FallbackBorder.Visibility = Visibility.Visible;
    }
}
