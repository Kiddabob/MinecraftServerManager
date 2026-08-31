using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

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

    public PlayerAvatar()
    {
        InitializeComponent();
        Unloaded += (_, _) => _loadVersion++;
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
        _ = ((PlayerAvatar)dependencyObject).LoadSkinAsync(args.NewValue as string);
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
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (loadVersion != _loadVersion)
            {
                return;
            }

            FaceImage.Source = bitmap;
            HatImage.Source = bitmap;
            SkinBorder.Visibility = Visibility.Visible;
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
        FaceImage.Source = null;
        HatImage.Source = null;
        SkinBorder.Visibility = Visibility.Collapsed;
        FallbackBorder.Visibility = Visibility.Visible;
    }
}
