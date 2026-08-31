using Microsoft.UI.Composition;
using Windows.Foundation.Metadata;

namespace MinecraftServerManager.Controls;

internal static class PixelArtRendering
{
    public static CompositionSurfaceBrush CreateSurfaceBrush(
        Compositor compositor,
        CompositionStretch stretch)
    {
        var brush = compositor.CreateSurfaceBrush();
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.Stretch = stretch;
        if (ApiInformation.IsPropertyPresent(
                "Microsoft.UI.Composition.CompositionSurfaceBrush",
                nameof(CompositionSurfaceBrush.SnapToPixels)))
        {
            brush.SnapToPixels = true;
        }

        return brush;
    }
}
