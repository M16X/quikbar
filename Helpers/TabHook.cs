using System.Runtime.InteropServices;

namespace quikbar.Helpers;

/// <summary>
/// Intercepts Tab at the WndProc level so it can trigger Quick AI.
/// WinUI's focus manager consumes Tab for focus navigation before
/// KeyDown/PreviewKeyDown handlers on controls ever fire, so a
/// control-level handler can never see it.
/// </summary>
public static class TabHook
{
    private const uint WM_KEYDOWN = 0x0100;
    private const nint VK_TAB = 0x09;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    private static SubclassProc? _proc; // kept alive for the app lifetime
    private static Func<bool>? _shouldHijack;
    private static Action? _onTab;

    public static void Install(nint hwnd, Func<bool> shouldHijack, Action onTab)
    {
        _shouldHijack = shouldHijack;
        _onTab = onTab;
        _proc = Proc;
        try { SetWindowSubclass(hwnd, _proc, 43, 0); } catch { }
    }

    private static nint Proc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        try
        {
            // Plain Tab only — never touch Alt+Tab (WM_SYSKEYDOWN) task switching.
            if (uMsg == WM_KEYDOWN && wParam == VK_TAB && (_shouldHijack?.Invoke() == true))
            {
                _onTab?.Invoke();
                return 0;
            }
        }
        catch { }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
