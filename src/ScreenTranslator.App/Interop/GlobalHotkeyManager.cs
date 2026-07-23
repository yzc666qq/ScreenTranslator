using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenTranslator.App.Interop;

internal enum GlobalHotkeyCommand
{
    ReselectRegion,
    StopTranslation
}

internal sealed class GlobalHotkeyManager : IDisposable
{
    internal const int ReselectRegionId = 0x5301;
    internal const int StopTranslationId = 0x5302;

    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyF1 = 0x70;
    private const uint VirtualKeyF2 = 0x71;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _f1Registered;
    private bool _f2Registered;

    public event Action<GlobalHotkeyCommand>? Pressed;

    public IReadOnlyList<string> Register(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Dispose();

        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法连接主窗口消息循环。");
        _source.AddHook(WindowProcedure);

        var unavailable = new List<string>(capacity: 2);
        _f1Registered = RegisterHotKey(_windowHandle, ReselectRegionId, ModNoRepeat, VirtualKeyF1);
        if (!_f1Registered)
        {
            unavailable.Add("F1");
        }

        _f2Registered = RegisterHotKey(_windowHandle, StopTranslationId, ModNoRepeat, VirtualKeyF2);
        if (!_f2Registered)
        {
            unavailable.Add("F2");
        }

        return unavailable;
    }

    internal static bool TryGetCommand(int hotkeyId, out GlobalHotkeyCommand command)
    {
        command = hotkeyId switch
        {
            ReselectRegionId => GlobalHotkeyCommand.ReselectRegion,
            StopTranslationId => GlobalHotkeyCommand.StopTranslation,
            _ => default
        };

        return hotkeyId is ReselectRegionId or StopTranslationId;
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            if (_f1Registered)
            {
                _ = UnregisterHotKey(_windowHandle, ReselectRegionId);
            }

            if (_f2Registered)
            {
                _ = UnregisterHotKey(_windowHandle, StopTranslationId);
            }
        }

        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = IntPtr.Zero;
        _f1Registered = false;
        _f2Registered = false;
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey && TryGetCommand(wordParameter.ToInt32(), out var command))
        {
            handled = true;
            Pressed?.Invoke(command);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
