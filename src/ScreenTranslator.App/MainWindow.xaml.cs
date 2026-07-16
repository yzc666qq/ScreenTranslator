using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ScreenTranslator.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _selectionArmed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ToggleSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _selectionArmed = !_selectionArmed;
        StatusText.Text = _selectionArmed ? "框选模式准备中" : "项目框架已就绪";
        SelectionButtonText.Text = _selectionArmed ? "取消准备" : "准备框选";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
