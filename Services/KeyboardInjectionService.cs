using System.Runtime.InteropServices;

namespace whispershortkey.Services;

public class KeyboardInjectionService
{
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const int SW_RESTORE = 9;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

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

    public void SendText(string text, bool useClipboardFallback, IntPtr targetWindowHandle)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (targetWindowHandle != IntPtr.Zero && IsWindow(targetWindowHandle))
        {
            if (IsIconic(targetWindowHandle))
            {
                ShowWindow(targetWindowHandle, SW_RESTORE);
            }
            SetForegroundWindow(targetWindowHandle);
            Thread.Sleep(200);
        }

        if (useClipboardFallback)
        {
            SendViaClipboard(text);
        }
        else
        {
            SendViaInput(text);
        }
    }

    private static void SendViaInput(string text)
    {
        Thread.Sleep(100);

        foreach (char c in text)
        {
            var inputs = new INPUT[2];

            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInputChecked(inputs);
            Thread.Sleep(1);
        }
    }

    private static void SendViaClipboard(string text)
    {
        Thread.Sleep(100);
        System.Windows.Clipboard.SetText(text);
        Thread.Sleep(80);
        try
        {
            SendKeyboardShortcut(VK_CONTROL, VK_V);
        }
        catch
        {
            System.Windows.Forms.SendKeys.SendWait("^v");
        }
        Thread.Sleep(100);
    }

    private static void SendKeyboardShortcut(ushort modifierKey, ushort key)
    {
        var inputs = new[]
        {
            KeyDown(modifierKey),
            KeyDown(key),
            KeyUp(key),
            KeyUp(modifierKey)
        };

        SendInputChecked(inputs);
    }

    private static void SendInputChecked(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput failed. Sent {sent} of {inputs.Length} inputs. Win32 error: {Marshal.GetLastWin32Error()}");
        }
    }

    private static INPUT KeyDown(ushort virtualKey)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static INPUT KeyUp(ushort virtualKey)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
