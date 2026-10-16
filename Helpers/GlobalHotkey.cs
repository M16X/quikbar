using System.Runtime.InteropServices;

namespace quikbar.Helpers;

/// <summary>
/// Global Alt+Space hook using a low-level keyboard hook (WH_KEYBOARD_LL).
///
/// NOTE: RegisterHotKey(MOD_ALT, VK_SPACE) does NOT work — Windows reserves
/// Alt+Space for the window system menu, so registration fails with
/// ERROR_HOTKEY_ALREADY_REGISTERED. PowerToys Command Palette solves this the
/// same way: a low-level hook that swallows the keystroke.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SPACE = 0x20;
    private const int LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public nint ExtraInfo;
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private nint _hook;
    private int _lastFire;
    private bool _spaceDown; // fire once per physical press, not per auto-repeat
    private HookProc? _proc; // kept alive for the lifetime of the hook
    private readonly Action _onHotkey;
    private bool _disposed;

    public GlobalHotkey(Action onHotkey) => _onHotkey = onHotkey;

    /// <summary>Installs the hook. Must be called on the UI thread (it pumps messages).</summary>
    public bool Start()
    {
        if (_hook != nint.Zero) return true;
        _proc = Proc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != nint.Zero;
    }

    private nint Proc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kb = Marshal.PtrToStructure<KbdStruct>(lParam);
                if (kb.VkCode == VK_SPACE && (kb.Flags & LLKHF_ALTDOWN) != 0)
                {
                    // A held Alt+Space produces auto-repeat KEYDOWNs (typematic delay is
                    // user-configurable, so time debounce alone can't stop them re-toggling
                    // the launcher back off). Track physical down/up instead.
                    if (!_spaceDown)
                    {
                        _spaceDown = true;
                        int now = Environment.TickCount;
                        if (now - _lastFire > 80) // anti-bounce only; real double-taps still toggle
                        {
                            _lastFire = now;
                            DebugLog.Write("hotkey: alt+space down -> fire");
                            try { _onHotkey(); } catch { }
                        }
                        else DebugLog.Write("hotkey: alt+space down -> bounce-suppressed");
                    }
                    return 1; // swallow: prevents the system menu from opening
                }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var kb = Marshal.PtrToStructure<KbdStruct>(lParam);
                if (kb.VkCode == VK_SPACE && _spaceDown)
                {
                    _spaceDown = false;
                    DebugLog.Write("hotkey: space up -> rearm");
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != nint.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }
        _proc = null;
    }
}
