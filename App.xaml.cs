using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using quikbar.Helpers;
using quikbar.Services;

namespace quikbar;

public partial class App : Application
{
    private static MainWindow? _launcher;
    private static SettingsWindow? _settings;
    private static TaskbarIcon? _tray;
    private static GlobalHotkey? _hotkey;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        SettingsStore.Load();

        var cmdArgs = Environment.GetCommandLineArgs();
        bool background = cmdArgs.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)
                                       || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        _launcher = new MainWindow();
        HookCloseToHide(_launcher);

        SetupTray();
        SetupHotkey(_launcher);

        if (background)
            WindowHelpers.Hide(_launcher);
        else
            _launcher.ShowLauncher();
    }

    // ---- windows ----

    public static void ShowSettings()
    {
        // Hide the launcher first: otherwise its focus-loss handler fights Settings for focus.
        try { _launcher?.HideLauncher(); } catch { }
        if (_settings is not null)
        {
            try { _settings.Activate(); } catch { }
            return;
        }
        _settings = new SettingsWindow();
        _settings.Activate();
    }

    public static void OnSettingsClosed() => _settings = null;

    public static void OnLauncherClosed()
    {
        // Real close (shouldn't normally happen since we cancel Closing).
        // Keep app alive via tray if tray exists; otherwise exit.
        if (_tray is null) Current.Exit();
    }

    public static void ToggleLauncher()
    {
        var l = _launcher;
        if (l is null) return;
        // The low-level hook fires on the UI thread, but dispatch anyway to be safe.
        l.DispatcherQueue.TryEnqueue(() => l.ToggleVisibility());
    }

    public static bool IsWindowVisible(Window w)
    {
        try
        {
            var hwnd = WindowHelpers.HwndOf(w);
            return IsWindowVisibleNative(hwnd);
        }
        catch { return true; }
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleNative(IntPtr hWnd);

    private static void HookCloseToHide(MainWindow launcher)
    {
        try
        {
            var appWindow = WindowHelpers.AppWindowOf(launcher);
            appWindow.Closing += (s, e) =>
            {
                // X button / Alt+F4 -> hide to tray instead of quitting
                e.Cancel = true;
                launcher.HideLauncher();
            };
        }
        catch { }
    }

    // ---- tray ----

    private static void SetupTray()
    {
        try
        {
            CloseTray();
            _tray = new TaskbarIcon
            {
                IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png")),
                ToolTipText = "quikbar — Alt+Space to open",
                ContextFlyout = BuildTrayMenu(),
            };
            _tray.LeftClickCommand = new RelayCommand(_ => ToggleLauncher());
            _tray.ForceCreate();
        }
        catch { }
    }

    private static MenuFlyout BuildTrayMenu()
    {
        var flyout = new MenuFlyout();
        FontFamily? geist = null;
        try { geist = new FontFamily(Services.AppFonts.Geist); } catch { }
        var open = new MenuFlyoutItem { Text = "Open launcher (Alt+Space)" };
        var settings = new MenuFlyoutItem { Text = "Settings" };
        var quit = new MenuFlyoutItem { Text = "Quit" };
        if (geist is not null)
        {
            open.FontFamily = geist;
            settings.FontFamily = geist;
            quit.FontFamily = geist;
        }
        open.Click += (_, _) => _launcher?.ShowLauncher();
        settings.Click += (_, _) => ShowSettings();
        quit.Click += (_, _) => Quit();
        flyout.Items.Add(open);
        flyout.Items.Add(settings);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(quit);
        return flyout;
    }

    private static void CloseTray()
    {
        try { _tray?.Dispose(); } catch { }
        _tray = null;
    }

    public static void Quit()
    {
        try { _hotkey?.Dispose(); } catch { }
        try { CloseTray(); } catch { }
        try { _settings?.Close(); } catch { }
        Current.Exit();
    }

    // ---- global hotkey Alt+Space (low-level hook; RegisterHotKey can't grab Alt+Space) ----

    private static void SetupHotkey(MainWindow launcher)
    {
        try
        {
            _hotkey?.Dispose();
            _hotkey = new GlobalHotkey(ToggleLauncher);
            if (!_hotkey.Start())
                System.Diagnostics.Debug.WriteLine("quikbar: keyboard hook failed to install");
        }
        catch { }
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object?> _run;
        public RelayCommand(Action<object?> run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _run(p);
    }
}
