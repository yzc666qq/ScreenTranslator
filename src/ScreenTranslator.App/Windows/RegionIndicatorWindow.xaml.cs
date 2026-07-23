using System.Windows;
using ScreenTranslator.App.Core;
using ScreenTranslator.App.Interop;

namespace ScreenTranslator.App.Windows;

public partial class RegionIndicatorWindow : Window
{
    public RegionIndicatorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindowBehavior.ConfigureOverlay(this, clickThrough: true);
    }

    public void SetRegion(ScreenRegion region)
    {
        var bounds = NativeWindowBehavior.PixelsToDips(region);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(1, bounds.Width);
        Height = Math.Max(1, bounds.Height);
    }
}
