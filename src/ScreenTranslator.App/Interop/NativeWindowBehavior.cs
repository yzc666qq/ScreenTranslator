using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Interop;

internal static class NativeWindowBehavior
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    public static void ConfigureOverlay(Window window, bool clickThrough)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExToolWindow;

        if (clickThrough)
        {
            style |= WsExTransparent | WsExNoActivate;
        }

        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    public static void ExcludeFromCapture(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    public static Rect PixelsToDips(ScreenRegion region)
    {
        var center = new NativePoint
        {
            X = region.X + region.Width / 2,
            Y = region.Y + region.Height / 2
        };

        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        var scaleX = 1d;
        var scaleY = 1d;

        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) == 0)
        {
            scaleX = dpiX / 96d;
            scaleY = dpiY / 96d;
        }

        return new Rect(
            region.X / scaleX,
            region.Y / scaleY,
            region.Width / scaleX,
            region.Height / scaleY);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
