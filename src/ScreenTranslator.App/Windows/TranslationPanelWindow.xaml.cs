using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ScreenTranslator.App.Interop;

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

    private void UpdateOpacityLabel()
    {
        if (OpacityText is not null)
        {
            OpacityText.Text = $"{OpacitySlider.Value:P0}";
        }
    }
}
