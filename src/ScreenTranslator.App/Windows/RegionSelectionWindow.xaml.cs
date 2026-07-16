using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenTranslator.App.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace ScreenTranslator.App.Windows;

public partial class RegionSelectionWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;

    public RegionSelectionWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public ScreenRegion? SelectedRegion { get; private set; }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootCanvas);
        _isSelecting = true;
        RootCanvas.CaptureMouse();
        SelectionBorder.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isSelecting)
        {
            UpdateSelection(e.GetPosition(RootCanvas));
        }
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var endPoint = e.GetPosition(RootCanvas);
        _isSelecting = false;
        RootCanvas.ReleaseMouseCapture();

        var left = Math.Min(_startPoint.X, endPoint.X);
        var top = Math.Min(_startPoint.Y, endPoint.Y);
        var right = Math.Max(_startPoint.X, endPoint.X);
        var bottom = Math.Max(_startPoint.Y, endPoint.Y);

        var pixelTopLeft = RootCanvas.PointToScreen(new Point(left, top));
        var pixelBottomRight = RootCanvas.PointToScreen(new Point(right, bottom));
        var region = new ScreenRegion(
            (int)Math.Round(pixelTopLeft.X),
            (int)Math.Round(pixelTopLeft.Y),
            (int)Math.Round(pixelBottomRight.X - pixelTopLeft.X),
            (int)Math.Round(pixelBottomRight.Y - pixelTopLeft.Y));

        if (region.Width < 24 || region.Height < 24)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedRegion = region;
        DialogResult = true;
    }

    private void RootCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void UpdateSelection(Point currentPoint)
    {
        var left = Math.Min(_startPoint.X, currentPoint.X);
        var top = Math.Min(_startPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _startPoint.X);
        var height = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
        SelectionSizeText.Text = $"{width:0} × {height:0}";
    }
}
