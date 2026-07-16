using System.Windows;
using ScreenTranslator.App.Core;
using ScreenTranslator.App.Interop;

namespace ScreenTranslator.App.Windows;

public partial class TranslationOverlayWindow : Window
{
    public TranslationOverlayWindow()
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

    public void SetTranslation(string text)
    {
        TranslationText.Text = text;
    }
}
