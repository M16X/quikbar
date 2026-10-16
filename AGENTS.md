# AGENTS.md — quikbar

WinUI 3 floating launcher (Raycast-style) + separate settings window. Single project, no tests, no CI.

## Build / verify

- `dotnet build -v q` from repo root is the only verification (no test/lint suite; GUI can't run headless).
- XAML compiler errors (`WMC9999`, `WMC1509`) are usually **cascades from a real C# error** — fix C# errors first, then rebuild before touching XAML.
- Target: `net8.0-windows10.0.26100`, Windows App SDK, single-project MSIX (`quikbar.csproj`). Two launch profiles: `quikbar (Package)` / `(Unpackaged)` (`Properties/launchSettings.json`).

## Architecture (not obvious from filenames)

- `MainWindow` = the launcher. `SettingsWindow` = settings. `MainPage.xaml(.cs)` is **dead template leftover** — nothing navigates to it; don't build on it.
- `App.xaml.cs` owns the tray icon, both windows, and quit. Closing the launcher is intercepted (`AppWindow.Closing` → cancel + hide); a real close exits the app.
- `Services/` = `SettingsStore` (JSON at `%AppData%\quikbar\settings.json`), `LlmService` (OpenAI-compatible `/chat/completions` + `/models`), `LauncherCommands` (query → Suggestions/Commands groups).
- Startup-at-login = `HKCU\...\Run` value `quikbar` with `--background` flag (see `SettingsWindow.ApplyLaunchAtStartup`).

## Gotchas — do not "simplify" these

- **Alt+Space**: `RegisterHotKey(MOD_ALT, VK_SPACE)` always fails (Windows reserves it). The low-level keyboard hook in `Helpers/GlobalHotkey.cs` is the fix — don't revert. It fires once per physical press via down/up tracking: time-only debounce loses to auto-repeat (a normal hold re-toggles the launcher back off).
- **Tab**: WinUI's focus manager eats Tab before any control key handler. `Helpers/TabHook.cs` (WndProc `SetWindowSubclass`) is the only working interception; it ignores `WM_SYSKEYDOWN` so Alt+Tab still works.
- **Show/hide**: always go through `Helpers/WindowHelpers` (`ShowWindow(SW_HIDE)` keeps the app alive in the tray). `ShowAndActivate` uses the `AttachThreadInput` foreground dance — a background process can't take focus with plain `Activate()`/`SetForegroundWindow` (they fail silently, so the hotkey looks dead globally). Don't simplify it away.
- Hide-on-focus-lost has a 2.5 s grace after `ShowLauncher` (tray-menu close and the OS yanking focus back after a global-hotkey summon both land in it) plus a one-time re-assert of activation — a Deactivated in that window re-shows instead of hiding. `ToggleVisibility` re-fronts a visible-but-buried window instead of hiding it.
- **Tray**: package is `H.NotifyIcon.WinUI` 2.3.0 but the namespace is `H.NotifyIcon` (no `.WinUI` suffix). Don't downgrade the version (triggers NU1603).
- **Glyphs** in `Services/LauncherCommands.cs` must stay as ASCII `\uE713`-style escapes for Segoe Fluent Icons. Never paste emoji or raw PUA chars — they silently become wrong codepoints (previously shipped `E8F7` instead of `E8B7`).
- New `.xaml` windows: set size/presenter via `AppWindow` + `OverlappedPresenter` in code-behind (see `WindowHelpers.MakeFloatingLauncher`), not XAML attributes.

## Shell environment

- The `bash` tool is **Git Bash on Windows**: `$VAR` gets expanded by bash — escape as `\$` in PowerShell one-liners. Prefer forward-slash absolute paths (`C:/Users/...`); quote backslash paths.
- No `python3` available. Use `Read`/`Edit`/`Write` for files, not `cat`/`sed`/`echo`.
