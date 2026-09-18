using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace whispershortkey.Services;

/// <summary>
/// Identity of the window that was focused when a recording started. The bare HWND is not
/// enough: handles are recycled, so by the time text is ready the same value can belong to
/// a completely different window in a different process.
/// </summary>
public readonly record struct WindowTarget(nint Hwnd, int ProcessId, long ProcessStartTicks)
{
    public bool IsEmpty => Hwnd == 0;
}

/// <summary>
/// Low-level input primitives. Policy - which window, or whether to fall back to the
/// clipboard - lives in <see cref="TextDeliveryService"/>.
/// </summary>
public class KeyboardInjectionService
{
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_V = 0x56;
    private const int SW_RESTORE = 9;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Windows often refuses a foreground change; polling is the only honest check.</summary>
    private const int FocusPollTimeoutMs = 400;
    private const int FocusPollStepMs = 40;

    /// <summary>Sent in chunks: one syscall per character is slow, one per transcript is fragile.</summary>
    private const int TypingChunkChars = 64;

    public static WindowTarget CaptureForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return default;

        GetWindowThreadProcessId(hwnd, out var pid);

        long startTicks = 0;
        try
        {
            startTicks = Process.GetProcessById((int)pid).StartTime.Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Protected or already-gone process: fall back to matching on the pid alone.
        }

        return new WindowTarget(hwnd, (int)pid, startTicks);
    }

    public static bool IsTargetAlive(WindowTarget target)
    {
        if (target.IsEmpty || !IsWindow(target.Hwnd)) return false;

        GetWindowThreadProcessId(target.Hwnd, out var pid);
        if ((int)pid != target.ProcessId) return false;

        if (target.ProcessStartTicks == 0) return true;

        try
        {
            return Process.GetProcessById(target.ProcessId).StartTime.Ticks == target.ProcessStartTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Brings the target to the foreground and verifies it actually got there. The return
    /// value of SetForegroundWindow lies in both directions, so the poll is the verdict -
    /// without it a transcript can be typed into whatever the user switched to instead.
    /// </summary>
    public bool TryFocus(WindowTarget target)
    {
        if (!IsTargetAlive(target)) return false;

        if (GetForegroundWindow() == target.Hwnd) return true;

        if (IsIconic(target.Hwnd))
            ShowWindow(target.Hwnd, SW_RESTORE);

        SetForegroundWindow(target.Hwnd);

        for (var waited = 0; waited < FocusPollTimeoutMs; waited += FocusPollStepMs)
        {
            if (GetForegroundWindow() == target.Hwnd) return true;
            Thread.Sleep(FocusPollStepMs);
        }

        return GetForegroundWindow() == target.Hwnd;
    }

    /// <summary>
    /// Waits briefly for the user to let go of the modifiers. The hotkey is Ctrl+Space, so
    /// Ctrl is very often still held when the transcript arrives - which would turn typed
    /// characters into control chords and Ctrl+V into something else entirely.
    /// </summary>
    public static void WaitForModifiersReleased(int timeoutMs = 500)
    {
        for (var waited = 0; waited < timeoutMs; waited += 20)
        {
            if (!AnyModifierDown()) return;
            Thread.Sleep(20);
        }
    }

    private static bool AnyModifierDown() =>
        IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    private static bool IsDown(ushort virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Puts text on the clipboard. Must run on an STA thread.</summary>
    public bool TrySetClipboard(string text)
    {
        try
        {
            // Retrying matters: clipboard managers, RDP sessions and Office routinely hold
            // the clipboard open, and a single SetText throws outright when they do.
            System.Windows.Forms.Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 50);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ThreadStateException)
        {
            return false;
        }
    }

    public bool TryPaste()
    {
        try
        {
            SendInputChecked([KeyDown(VK_CONTROL), KeyDown(VK_V), KeyUp(VK_V), KeyUp(VK_CONTROL)]);
            return true;
        }
        catch (InvalidOperationException)
        {
            try
            {
                System.Windows.Forms.SendKeys.SendWait("^v");
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                return false;
            }
        }
    }

    public bool TryType(string text)
    {
        try
        {
            for (var offset = 0; offset < text.Length; offset += TypingChunkChars)
            {
                var length = Math.Min(TypingChunkChars, text.Length - offset);
                var inputs = new INPUT[length * 2];

                for (var i = 0; i < length; i++)
                {
                    var c = text[offset + i];
                    inputs[i * 2] = UnicodeKey(c, KEYEVENTF_UNICODE);
                    inputs[i * 2 + 1] = UnicodeKey(c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
                }

                SendInputChecked(inputs);
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static INPUT UnicodeKey(char c, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static INPUT KeyDown(ushort virtualKey) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = virtualKey, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero }
        }
    };

    private static INPUT KeyUp(ushort virtualKey) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = virtualKey, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
        }
    };

    private static void SendInputChecked(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput failed. Sent {sent} of {inputs.Length} inputs. Win32 error: {Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
