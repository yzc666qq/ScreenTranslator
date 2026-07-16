using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ScreenTranslator.App.Interop;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ScreenTranslator.App.Windows;

public partial class TranslationPanelWindow : Window
{
    private bool _allowClose;

    public TranslationPanelWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindowBehavior.ConfigureOverlay(this, clickThrough: false);
        Opacity = OpacitySlider.Value;
        UpdateOpacityLabel();
    }

    public void SetTranslation(string text)
    {
        TranslationText.Text = text;
    }

    public void SetTypography(string fontFamily, double fontSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        TranslationText.FontFamily = new System.Windows.Media.FontFamily(fontFamily);
        TranslationText.FontSize = Math.Clamp(fontSize, 10, 48);
        TranslationText.LineHeight = TranslationText.FontSize * 1.45;
    }

    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && LockToggle.IsChecked != true)
        {
            DragMove();
        }
    }

    private void LockToggle_Changed(object sender, RoutedEventArgs e)
    {
        var isLocked = LockToggle.IsChecked == true;
        ResizeMode = isLocked
            ? ResizeMode.NoResize
            : ResizeMode.CanResizeWithGrip;
        LockToggle.Content = isLocked ? "已锁定" : "锁定";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
        UpdateOpacityLabel();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ControlDock_MouseEnter(object sender, MouseEventArgs e)
    {
        ControlDock.Opacity = 0.96;
    }

    private void ControlDock_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!OpacityPopup.IsOpen)
        {
            ControlDock.Opacity = 0.18;
        }
    }

    private void OpacityPopup_Closed(object? sender, EventArgs e)
    {
        if (!ControlDock.IsMouseOver)
        {
            ControlDock.Opacity = 0.18;
        }
    }

    private void UpdateOpacityLabel()
    {
        if (OpacityText is not null)
        {
            OpacityText.Text = $"{OpacitySlider.Value:P0}";
        }
    }
}
