using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using quikbar.Services;
using WinRT.Interop;

namespace quikbar;

public sealed partial class SettingsWindow : Window
{
    private readonly LlmService _llm = new();
    private readonly DispatcherQueueTimer _tempSaveTimer;
    private bool _loading;

    // Minimum window size, enforced via WM_GETMINMAXINFO (same subclass pattern as TabHook).
    private const uint WM_GETMINMAXINFO = 0x24;
    private static int s_minWidth = 1120;
    private static int s_minHeight = 880;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint Reserved;
        public WinPoint MaxSize;
        public WinPoint MaxPosition;
        public WinPoint MinTrackSize;
        public WinPoint MaxTrackSize;
    }

    private static readonly SubclassProc s_minSizeProc = MinSizeProc;

    private static nint MinSizeProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        try
        {
            if (uMsg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.MinTrackSize = new WinPoint { X = s_minWidth, Y = s_minHeight };
                Marshal.StructureToPtr(mmi, lParam, false);
            }
        }
        catch { }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRoot);

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.SetIcon("Assets/AppIcon.ico");
            appWindow.Title = "quikbar Settings";
            var initialSize = new Windows.Graphics.SizeInt32(1280, 960);
            appWindow.Resize(initialSize);
            s_minWidth = initialSize.Width;
            s_minHeight = initialSize.Height;
            if (appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsMaximizable = true;
            }
            SetWindowSubclass(hwnd, s_minSizeProc, 44, 0);
        }
        catch { }

        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = v is null ? "" : $"v{v.ToString(3)}";
        }
        catch { }

        _tempSaveTimer = DispatcherQueue.CreateTimer();
        _tempSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _tempSaveTimer.Tick += (_, _) =>
        {
            SettingsStore.Update(s => s.Temperature = Math.Round(TempSlider.Value, 2));
        };

        LoadFromSettings();
        Closed += (_, _) => App.OnSettingsClosed();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        SetNav(NavProvider, NavProviderIcon, NavProviderLabel, PageProvider, ReferenceEquals(sender, NavProvider));
        SetNav(NavLauncher, NavLauncherIcon, NavLauncherLabel, PageLauncher, ReferenceEquals(sender, NavLauncher));
        SetNav(NavGeneration, NavGenerationIcon, NavGenerationLabel, PageGeneration, ReferenceEquals(sender, NavGeneration));
        SetNav(NavAbout, NavAboutIcon, NavAboutLabel, PageAbout, ReferenceEquals(sender, NavAbout));
    }

    private static void SetNav(Button nav, FontIcon icon, TextBlock label, UIElement page, bool selected)
    {
        var res = Application.Current.Resources;
        nav.Background = selected
            ? (Brush)res["ControlFillColorDefaultBrush"]
            : new SolidColorBrush(Colors.Transparent);
        var fg = (Brush)res[selected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        nav.Foreground = fg;
        icon.Foreground = fg;
        label.Foreground = fg;
        label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsStore.Current;
            EndpointBox.Text = s.Endpoint;
            ApiKeyBox.Password = s.ApiKey;
            ModelBox.ItemsSource = s.Models.Count > 0 ? s.Models : new List<string> { s.DefaultModel };
            ModelBox.Text = s.DefaultModel;
            TempSlider.Value = s.Temperature;
            TempValue.Text = s.Temperature.ToString("0.0");
            SystemPromptBox.Text = s.SystemPrompt;
            HideOnFocusLostToggle.IsOn = s.HideOnFocusLost;
            LaunchAtStartupToggle.IsOn = s.LaunchAtStartup;
        }
        finally { _loading = false; }
    }

    private void EndpointBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var normalized = LlmService.NormalizeEndpoint(EndpointBox.Text);
        if (normalized != EndpointBox.Text) EndpointBox.Text = normalized;
        SettingsStore.Update(s => s.Endpoint = normalized);
    }

    private void ApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.ApiKey = ApiKeyBox.Password.Trim());
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveModel();

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e) => SaveModel();

    private void SaveModel()
    {
        if (_loading) return;
        var typed = ModelBox.Text.Trim();
        if (string.IsNullOrEmpty(typed)) return;
        SettingsStore.Update(s =>
        {
            s.DefaultModel = typed;
            if (ModelBox.ItemsSource is IEnumerable<string> items)
            {
                var list = items.Distinct().ToList();
                if (!list.Contains(typed)) list.Add(typed);
                s.Models = list;
            }
            else s.Models = new List<string> { typed };
        });
    }

    private void SystemPromptBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.SystemPrompt = SystemPromptBox.Text.Trim());
    }

    private void HideOnFocusLostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.HideOnFocusLost = HideOnFocusLostToggle.IsOn);
    }

    private void LaunchAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.LaunchAtStartup = LaunchAtStartupToggle.IsOn);
        ApplyLaunchAtStartup(LaunchAtStartupToggle.IsOn);
    }

    private void TempSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (TempValue is not null)
            TempValue.Text = e.NewValue.ToString("0.0");
        if (_loading) return;
        _tempSaveTimer.Stop();
        _tempSaveTimer.Start();
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        ModelsStatus.Text = "Fetching models…";
        RefreshModelsButton.IsEnabled = false;
        try
        {
            var models = await _llm.ListModelsAsync(EndpointBox.Text, ApiKeyBox.Password);
            if (models.Count == 0) ModelsStatus.Text = "No models returned.";
            else
            {
                ModelBox.ItemsSource = models;
                if (!models.Contains(ModelBox.Text) && models.Count > 0)
                    ModelBox.SelectedIndex = 0;
                ModelsStatus.Text = $"Found {models.Count} models.";
                SaveModel();
            }
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = "Failed: " + ex.Message;
        }
        finally { RefreshModelsButton.IsEnabled = true; }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestStatus.Text = "Testing…";
        TestButton.IsEnabled = false;
        try
        {
            var model = ModelBox.Text.Trim();
            if (string.IsNullOrEmpty(model)) model = "gpt-4o-mini";
            var answer = await _llm.AskAsync(EndpointBox.Text, ApiKeyBox.Password, model,
                TempSlider.Value, "You are a test.", new List<(string, string)>(), "Reply with: ok");
            TestStatus.Text = "OK: " + (answer.Length > 120 ? answer[..120] + "…" : answer);
        }
        catch (Exception ex)
        {
            TestStatus.Text = "Failed: " + ex.Message;
        }
        finally { TestButton.IsEnabled = true; }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar", "settings.json");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"/select,{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static void ApplyLaunchAtStartup(bool enable)
    {
        try
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rk = Registry.CurrentUser.OpenSubKey(key, writable: true);
            if (enable)
            {
                var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe) && rk is not null)
                    rk.SetValue("quikbar", $"\"{exe}\" --background");
            }
            else rk?.DeleteValue("quikbar", throwOnMissingValue: false);
        }
        catch { }
    }
}
