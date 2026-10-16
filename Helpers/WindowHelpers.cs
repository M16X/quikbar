using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace quikbar.Helpers;

public static class WindowHelpers
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public static IntPtr HwndOf(Microsoft.UI.Xaml.Window window) =>
        WindowNative.GetWindowHandle(window);

    public static AppWindow AppWindowOf(Microsoft.UI.Xaml.Window window)
    {
        var hwnd = HwndOf(window);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    public static void CenterOnScreen(Microsoft.UI.Xaml.Window window, int width, int height)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            MoveToCenter(window);
        }
        catch { }
    }

    /// <summary>Centers the window without changing its size (preserves user resizing).</summary>
    public static void MoveToCenter(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            var size = appWindow.Size;
            var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            int x = work.X + (work.Width - size.Width) / 2;
            int y = work.Y + (work.Height - size.Height) / 3; // slightly above center, palette-style
            appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }
    }

    public static bool IsForeground(Microsoft.UI.Xaml.Window window)
    {
        try { return GetForegroundWindow() == HwndOf(window); }
        catch { return true; }
    }

    public static void ShowAndActivate(Microsoft.UI.Xaml.Window window)
    {
        try { AppWindowOf(window).Show(); } catch { }
        var hwnd = HwndOf(window);
        ShowWindow(hwnd, SW_RESTORE);
        ShowWindow(hwnd, SW_SHOW);
        try
        {
            // TOPMOST while visible: even when the OS locks foreground changes (user busy
            // in another app), the launcher still surfaces visibly. Cleared on hide.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            // A background process cannot take foreground on its own: SetForegroundWindow
            // and Activate silently fail, leaving the launcher behind/unfocused. Borrowing
            // the foreground thread's input rights via AttachThreadInput makes it succeed.
            // Must run on the UI thread.
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint ourThread = GetCurrentThreadId();
            bool attached = fgThread != ourThread && fgThread != 0 && AttachThreadInput(ourThread, fgThread, true);
            try
            {
                SetForegroundWindow(hwnd);
                window.Activate();
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThread, fgThread, false);
            }
        }
        catch
        {
            try { window.Activate(); } catch { }
        }
    }

    public static void Hide(Microsoft.UI.Xaml.Window window)
    {
        var hwnd = HwndOf(window);
        try { SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE); } catch { }
        ShowWindow(hwnd, SW_HIDE);
    }

    public static void MakeFloatingLauncher(Microsoft.UI.Xaml.Window window, int width = 1120, int height = 720)
    {
        try
        {
            var appWindow = AppWindowOf(window);
            appWindow.SetIcon("Assets/AppIcon.ico");
            appWindow.Title = "quikbar";
            appWindow.IsShownInSwitchers = false; // palette window: no taskbar / Alt+Tab entry
            if (appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;      // user-resizable
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.SetBorderAndTitleBar(true, false); // thin border (resize handles) + no title bar
            }
            CenterOnScreen(window, width, height);
        }
        catch { }
    }
}
