using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fo4Cli;

// Sends real keyboard scan codes to the game window (Fallout 4 reads input via DirectInput, which ignores
// window messages / SendKeys). Used e.g. to dismiss the "this save relies on content..." popup after `load`.
static class Keys
{
    static readonly Dictionary<string, ushort> Scan = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x1C, ["esc"] = 0x01, ["escape"] = 0x01, ["tilde"] = 0x29, ["console"] = 0x29,
        ["space"] = 0x39, ["tab"] = 0x0F, ["e"] = 0x12, ["r"] = 0x13, ["y"] = 0x15, ["n"] = 0x31,
        ["up"] = 0x48, ["down"] = 0x50, ["left"] = 0x4B, ["right"] = 0x4D,
    };
    static readonly HashSet<ushort> Extended = [0x48, 0x50, 0x4B, 0x4D];

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_EXTENDEDKEY = 0x1;

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public static int Press(List<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("usage: fo4-cli key <enter|esc|tilde|space|tab|e|up|down|...> [...]");
        var game = Process.GetProcessesByName("Fallout4").FirstOrDefault()
                   ?? throw new F4ExecUnavailable("Fallout 4 is not running");
        if (game.MainWindowHandle != IntPtr.Zero)
        {
            ShowWindow(game.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(game.MainWindowHandle);
            Thread.Sleep(150);
        }
        foreach (var name in names)
        {
            if (!Scan.TryGetValue(name, out var sc)) throw new ArgumentException($"unknown key '{name}'");
            uint ext = Extended.Contains(sc) ? KEYEVENTF_EXTENDEDKEY : 0;
            var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = sc, dwFlags = KEYEVENTF_SCANCODE | ext } } };
            var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = sc, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | ext } } };
            SendInput(1, [down], Marshal.SizeOf<INPUT>());
            Thread.Sleep(60);
            SendInput(1, [up], Marshal.SizeOf<INPUT>());
            Thread.Sleep(120);
            Console.WriteLine($"pressed {name}");
        }
        return 0;
    }
}
