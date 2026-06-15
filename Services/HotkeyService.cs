using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace whispershortkey.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_SPACE = 0x20;

    private const int HotkeyId = 9000;
    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? HotkeyPressed;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_NOREPEAT, VK_SPACE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
