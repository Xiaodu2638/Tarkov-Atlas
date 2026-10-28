using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace TarkovAtlas.Services;

public static class HotkeyFormatter
{
    public static string Format(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.Space => "Space",
        Key.Tab => "Tab",
        _ => key.ToString(),
    };

    public static int? ToVirtualKey(string token)
    {
        token = token.Trim();
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (token.Equals("Space", StringComparison.OrdinalIgnoreCase)) return 0x20;
        if (token.Equals("Tab", StringComparison.OrdinalIgnoreCase)) return 0x09;
        if (token.Equals("Enter", StringComparison.OrdinalIgnoreCase)) return 0x0D;
        if (token.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return 0x1B;
        if (token.Equals("`", StringComparison.OrdinalIgnoreCase)) return 0xC0;
        if (token.Equals("-", StringComparison.OrdinalIgnoreCase)) return 0xBD;
        if (token.Equals("=", StringComparison.OrdinalIgnoreCase)) return 0xBB;
        if (token.Equals("[", StringComparison.OrdinalIgnoreCase)) return 0xDB;
        if (token.Equals("]", StringComparison.OrdinalIgnoreCase)) return 0xDD;
        if (token.Equals("\\", StringComparison.OrdinalIgnoreCase)) return 0xDC;
        if (token.Equals(";", StringComparison.OrdinalIgnoreCase)) return 0xBA;
        if (token.Equals("'", StringComparison.OrdinalIgnoreCase)) return 0xDE;
        if (token.Equals(",", StringComparison.OrdinalIgnoreCase)) return 0xBC;
        if (token.Equals(".", StringComparison.OrdinalIgnoreCase)) return 0xBE;
        if (token.Equals("/", StringComparison.OrdinalIgnoreCase)) return 0xBF;

        if (token.Length >= 2 && token[0] is 'F' or 'f' && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
            return 0x6F + f;

        if (token.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[6..], out var num) && num is >= 0 and <= 9)
            return 0x60 + num;

        return null;
    }

    public static bool IsValid(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        var key = parts[^1];
        return ToVirtualKey(key).HasValue;
    }

    public static (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win) Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (0, false, false, false, false);
        var ctrl = parts.Any(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase));
        var alt = parts.Any(p => p.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        var shift = parts.Any(p => p.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var win = parts.Any(p => p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Meta", StringComparison.OrdinalIgnoreCase));
        var vk = ToVirtualKey(parts[^1]) ?? 0;
        return (vk, ctrl, alt, shift, win);
    }
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _callback;
    private readonly HashSet<int> _down = new();
    private IntPtr _hook;
    private readonly object _gate = new();

    private (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win) _overlay;
    private (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win) _lock;

    public string LastError { get; private set; } = "";
    public event EventHandler? OverlayToggleRequested;
    public event EventHandler? LockToggleRequested;

    public GlobalHotkeyService()
    {
        _callback = HookCallback;
    }

    public void Update(string overlayHotkey, string lockHotkey)
    {
        _overlay = HotkeyFormatter.Parse(overlayHotkey);
        _lock = HotkeyFormatter.Parse(lockHotkey);
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                LastError = "RegisterHotkey failed: " + Marshal.GetLastWin32Error();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppPaths.LogCrash(ex);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var vk = Marshal.ReadInt32(lParam);
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                bool fresh;
                lock (_gate) fresh = _down.Add(vk);
                if (fresh)
                {
                    var ctrl = Down(VkControl);
                    var alt = Down(VkMenu);
                    var shift = Down(VkShift);
                    var win = Down(VkLWin) || Down(VkRWin);

                    if (Matches(_overlay, vk, ctrl, alt, shift, win))
                        Dispatcher.UIThread.Post(() => OverlayToggleRequested?.Invoke(this, EventArgs.Empty));
                    else if (Matches(_lock, vk, ctrl, alt, shift, win))
                        Dispatcher.UIThread.Post(() => LockToggleRequested?.Invoke(this, EventArgs.Empty));
                }
            }
            else if (msg is WmKeyUp or WmSysKeyUp)
            {
                lock (_gate) _down.Remove(vk);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Matches((int Vk, bool Ctrl, bool Alt, bool Shift, bool Win) hotkey,
        int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (hotkey.Vk == 0 || hotkey.Vk != vk) return false;
        return hotkey.Ctrl == ctrl && hotkey.Alt == alt && hotkey.Shift == shift && hotkey.Win == win;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
    }
}
