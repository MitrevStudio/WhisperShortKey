using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace whispershortkey.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_SPACE = 0x20;
    private const uint VK_ESCAPE = 0x1B;

    private const int ToggleHotkeyId = 9000;
    private const int CancelHotkeyId = 9001;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _cancelRegistered;

    public event Action? HotkeyPressed;

    /// <summary>Escape, registered only while a recording is running.</summary>
    public event Action? CancelPressed;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Returns false when the hotkey could not be claimed - Ctrl+Space is commonly owned by
    /// an IME or another tool, and silently doing nothing leaves the user with no idea why.
    /// </summary>
    public bool Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        return RegisterHotKey(hwnd, ToggleHotkeyId, MOD_CONTROL | MOD_NOREPEAT, VK_SPACE);
    }

    /// <summary>
    /// Claims Escape for the duration of a recording. Taking a bare key globally is only
    /// acceptable because it lasts seconds and the user is mid-dictation.
    /// </summary>
    public void EnableCancelHotkey()
    {
        if (_cancelRegistered || _hwnd == IntPtr.Zero) return;
        _cancelRegistered = RegisterHotKey(_hwnd, CancelHotkeyId, MOD_NOREPEAT, VK_ESCAPE);
    }

    public void DisableCancelHotkey()
    {
        if (!_cancelRegistered || _hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, CancelHotkeyId);
        _cancelRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case ToggleHotkeyId:
                HotkeyPressed?.Invoke();
                handled = true;
                break;

            case CancelHotkeyId:
                CancelPressed?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DisableCancelHotkey();
            UnregisterHotKey(_hwnd, ToggleHotkeyId);
        }

        _source?.RemoveHook(WndProc);
    }
}
