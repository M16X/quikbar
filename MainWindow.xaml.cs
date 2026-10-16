using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using quikbar.Helpers;
using quikbar.Services;
using Windows.System;

namespace quikbar;

public sealed partial class MainWindow : Window
{
    private List<LauncherItem> _suggestions = new();
    private List<LauncherItem> _commands = new();
    private int _sel = -1; // global index over suggestions + commands

    private readonly LlmService _llm = new();
    private readonly CurrencyService _fx = new();
    private readonly DictionaryService _dict = new();
    private List<quikbar.Models.ChatConversation> _convos = new();
    private quikbar.Models.ChatConversation? _convo;
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _lookupCts;
    private bool _aiMode;
    private string _aiRawAnswer = "";
    private int _shownTick; // suppress hide-on-focus-lost right after showing (tray menu / focus yank-back races)
    private bool _refocusedAfterShow; // re-assert activation at most once per show
    private int _queryToken;
    private bool _centeredOnce; // center only on first show; afterwards keep user's position
    private string? _armedKey; // destructive-row two-step confirm: armed row key, else null
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _armTimer;
    private bool _resizing;
    private Windows.Foundation.Point _resizeStart;
    private Windows.Graphics.SizeInt32 _resizeOrig;
    private const int MinLauncherWidth = 560;
    private const int MinLauncherHeight = 400;

    // Minimum window size, enforced via WM_GETMINMAXINFO (same subclass pattern as TabHook/SettingsWindow).
    private const uint WM_GETMINMAXINFO = 0x24;
    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

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
                mmi.MinTrackSize = new WinPoint { X = MinLauncherWidth, Y = MinLauncherHeight };
                Marshal.StructureToPtr(mmi, lParam, false);
            }
        }
        catch { }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        WindowHelpers.MakeFloatingLauncher(this);

        Activated += MainWindow_Activated;
        Closed += (_, _) => App.OnLauncherClosed();

        SearchBox.KeyDown += SearchBox_KeyDown;
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;

        // Tab is consumed by WinUI focus navigation before it reaches key handlers,
        // so hook it at the WndProc level for Quick AI (search mode only).
        try { TabHook.Install(WindowHelpers.HwndOf(this), shouldHijack: () => !_aiMode, onTab: TriggerQuickAi); } catch { }
        try { SetWindowSubclass(WindowHelpers.HwndOf(this), s_minSizeProc, 45, 0); } catch { }

        // Footer doubles as the window drag region in search mode (all children passive).
        try { SetTitleBar(FooterGrid); } catch { }

        _armTimer = DispatcherQueue.CreateTimer();
        _armTimer.Interval = TimeSpan.FromSeconds(8);
        _armTimer.Tick += (_, _) => DisarmConfirm();

        _convos = ChatStore.Load();
        // One-time migration: rewrite the file without empties stored by older builds.
        try { if (File.Exists(ChatStore.DefaultPath)) ChatStore.Save(_convos); } catch { }
        RefreshResults("");
        // Pre-warm currency rates in background so conversions work offline-fast.
        _ = Task.Run(async () =>
        {
            try
            {
                var cache = CurrencyService.LoadCache();
                if (cache is null || CurrencyService.IsStale(cache.FetchedAt, DateTime.UtcNow))
                    await _fx.RefreshAsync();
            }
            catch { }
        });
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void TriggerQuickAi()
    {
        try
        {
            var q = (SearchBox.Text ?? "").Trim();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (string.IsNullOrEmpty(q))
                    EnterBlankAiView();
                else
                    _ = AskAiAsync(q, followUp: false);
            });
        }
        catch { }
    }

    // ---- public API used by App/tray/hotkey ----

    public void ShowLauncher([CallerMemberName] string caller = "")
    {
        DebugLog.Write($"show [{caller}]");
        _shownTick = Environment.TickCount;
        _refocusedAfterShow = false;
        if (!_centeredOnce)
        {
            WindowHelpers.MoveToCenter(this); // first show only; afterwards keep user's position
            _centeredOnce = true;
        }
        WindowHelpers.ShowAndActivate(this);
        if (_aiMode)
            AiInput.Focus(FocusState.Programmatic);
        else
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
    }

    public void HideLauncher([CallerMemberName] string caller = "")
    {
        DebugLog.Write($"hide [{caller}]");
        _armedKey = null;
        try { _armTimer?.Stop(); } catch { }
        WindowHelpers.Hide(this);
    }

    public void ToggleVisibility()
    {
        // A visible-but-buried launcher must come back to the front, not hide.
        bool visible = App.IsWindowVisible(this);
        bool fg = WindowHelpers.IsForeground(this);
        DebugLog.Write($"toggle [visible={visible} fg={fg}]");
        if (!visible || !fg) ShowLauncher();
        else HideLauncher();
    }

    // ---- events ----

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DebugLog.Write($"activated: {args.WindowActivationState}");
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && SettingsStore.Current.HideOnFocusLost
            && AiSpinner.IsActive == false)
        {
            if (Environment.TickCount - _shownTick <= 2500)
            {
                // Likely the OS yanking focus back right after a global-hotkey summon —
                // re-assert once instead of vanishing. (Tray-menu dismissal lands here too.)
                if (!_refocusedAfterShow)
                {
                    _refocusedAfterShow = true;
                    DebugLog.Write("deactivated in grace -> re-assert");
                    try
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            WindowHelpers.ShowAndActivate(this);
                            FocusSearch();
                        });
                    }
                    catch { }
                }
                return;
            }
            DebugLog.Write("deactivated past grace -> hide");
            HideLauncher();
        }
    }

    private void FocusSearch()
    {
        try
        {
            if (_aiMode) AiInput.Focus(FocusState.Programmatic);
            else { SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); }
        }
        catch { }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_aiMode) return;
        RefreshResults(SearchBox.Text);
    }

    private void RefreshResults(string text)
    {
        // Any keystroke disarms a pending destructive-row confirmation.
        _armedKey = null;
        try { _armTimer?.Stop(); } catch { }
        var (sug, cmd) = LauncherCommands.QueryGrouped(text);
        // Placeholders for lookups with no cached value yet (logic unit-tested).
        foreach (var ph in LauncherCommands.PlaceholderRowsFor(text, cmd))
            cmd.Insert(0, ph);
        _suggestions = sug;
        _commands = cmd.Take(9).ToList();
        SuggestionsList.ItemsSource = _suggestions;
        CommandsList.ItemsSource = _commands;
        SuggestionsHeader.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsList.Visibility = SuggestionsHeader.Visibility;
        CommandsHeader.Visibility = _commands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandsList.Visibility = CommandsHeader.Visibility;
        EmptyText.Visibility = TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _sel = TotalCount > 0 ? 0 : -1;
        ApplySelection();

        // Async fill-in for currency/dictionary misses (debounced by token).
        var token = ++_queryToken;
        var snapshot = text;
        _lookupCts?.Cancel();
        _lookupCts = new CancellationTokenSource();
        var ct = _lookupCts.Token;
        _ = FillLookupsAsync(snapshot, token, ct);
    }

    private async Task FillLookupsAsync(string text, int token, CancellationToken ct)
    {
        try
        {
            bool changed = false;
            // Currency: refresh rates if parse hits but conversion failed.
            if (CurrencyParser.TryParse(text, out var q))
            {
                var cache = CurrencyService.LoadCache();
                if (cache is null || !CurrencyService.TryConvert(q.Amount, q.From, q.To, cache, out _))
                {
                    var fresh = await _fx.RefreshAsync(ct);
                    if (fresh is not null)
                    {
                        LauncherCommands.NoteFxRefreshed(fresh);
                        changed = true;
                    }
                }
            }
            // Dictionary: fetch on prefix miss.
            if (DictionaryService.TryParseQuery(text, out var word))
            {
                var dc = DictionaryService.LoadCache();
                if (!dc.ContainsKey(word))
                {
                    var entries = await _dict.FetchAsync(word, ct);
                    if (entries is not null && entries.Count > 0) changed = true;
                    else if (entries is not null) changed = true; // 404 cached as empty? still refresh to show "not found"
                }
            }
            if (!changed || ct.IsCancellationRequested) return;
            await Task.Delay(50, ct); // let fast typing settle
            if (token != _queryToken || ct.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token != _queryToken) return;
                var cur = _aiMode ? "" : (SearchBox.Text ?? "");
                if (!string.Equals(cur, text, StringComparison.Ordinal)) return;
                var (sug, cmd) = LauncherCommands.QueryGrouped(text);
                if (cmd.All(c => c.Kind != LauncherItemKind.Dictionary)
                    && DictionaryService.TryParseQuery(text, out var w2))
                {
                    var dc2 = DictionaryService.LoadCache();
                    if (dc2.TryGetValue(w2, out var hit) && hit.Entries.Count > 0)
                    { /* cached now, QueryGrouped already added */ }
                    else
                        cmd.Insert(0, new LauncherItem(LauncherItemKind.Dictionary,
                            $"No definition found for “{w2}”", "Dictionary", "\uE8A5",
                            Payload: "", Hint: "Dictionary"));
                }
                _suggestions = sug;
                _commands = cmd.Take(9).ToList();
                SuggestionsList.ItemsSource = _suggestions;
                CommandsList.ItemsSource = _commands;
                SuggestionsHeader.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                SuggestionsList.Visibility = SuggestionsHeader.Visibility;
                CommandsHeader.Visibility = _commands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                CommandsList.Visibility = CommandsHeader.Visibility;
                EmptyText.Visibility = TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                _sel = TotalCount > 0 ? 0 : -1;
                ApplySelection();
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        RefreshResults("");
        SearchBox.Focus(FocusState.Programmatic);
    }

    private int TotalCount => _suggestions.Count + _commands.Count;

    private LauncherItem? SelectedItem()
    {
        if (_sel < 0 || _sel >= TotalCount) return null;
        return _sel < _suggestions.Count ? _suggestions[_sel] : _commands[_sel - _suggestions.Count];
    }

    private void ApplySelection()
    {
        if (_sel < 0 || TotalCount == 0)
        {
            SuggestionsList.SelectedIndex = -1;
            CommandsList.SelectedIndex = -1;
            return;
        }
        if (_sel < _suggestions.Count)
        {
            SuggestionsList.SelectedIndex = _sel;
            CommandsList.SelectedIndex = -1;
            SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
        }
        else
        {
            SuggestionsList.SelectedIndex = -1;
            CommandsList.SelectedIndex = _sel - _suggestions.Count;
            CommandsList.ScrollIntoView(CommandsList.SelectedItem);
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down) { MoveSelection(1); e.Handled = true; }
        else if (e.Key == VirtualKey.Up) { MoveSelection(-1); e.Handled = true; }
        else if (e.Key == VirtualKey.Tab)
        {
            // "Quick AI": jump straight into the AI view with the current query.
            e.Handled = true;
            TriggerQuickAi();
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunSelectedAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideLauncher();
        }
    }

    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LauncherItem item)
            await RunItemAsync(item);
    }

    private async Task RunSelectedAsync()
    {
        var item = SelectedItem();
        if (item is null) return;
        await RunItemAsync(item);
    }

    private async Task RunItemAsync(LauncherItem item)
    {
        var key = EnterActionResolver.ConfirmKey(item.Kind, item.Title, item.Payload);
        bool armed = item.RequiresConfirmation && _armedKey == key;
        var action = EnterActionResolver.Resolve(
            item.Kind, item.Title, item.Payload, item.ActionAsync is not null,
            requiresConfirmation: item.RequiresConfirmation, armed: armed);
        if (action.Kind != EnterActionKind.ConfirmRequired)
        {
            _armedKey = null;
            try { _armTimer?.Stop(); } catch { }
        }
        switch (action.Kind)
        {
            case EnterActionKind.ConfirmRequired:
                ArmItem(item, key);
                break;
            case EnterActionKind.AskAi:
                await AskAiAsync(item.Payload ?? SearchBox.Text, followUp: false);
                break;
            case EnterActionKind.ShowSettings:
                App.ShowSettings();
                break;
            case EnterActionKind.CopyAndHide:
                CopyToClipboard(action.CopyText ?? "");
                HideLauncher();
                break;
            case EnterActionKind.FetchDictionaryAndRefresh:
            {
                // Placeholder row: fetch then refresh inline, launcher stays open.
                var entries = await _dict.FetchAsync(action.Word ?? "");
                RefreshResults(SearchBox.Text);
                if (entries is null)
                {
                    // network failed; keep launcher open showing placeholder
                }
                break;
            }
            case EnterActionKind.RunAndHide:
                try
                {
                    if (item.ActionAsync is not null)
                        await item.ActionAsync();
                }
                catch { }
                HideLauncher();
                break;
            case EnterActionKind.None:
            default:
                break; // e.g. "No definition found" or still-loading row: stay open
        }
    }

    // ---- destructive-row two-step confirmation ----

    private void ArmItem(LauncherItem item, string key)
    {
        var i = _commands.FindIndex(c => ReferenceEquals(c, item));
        if (i < 0)
            i = _commands.FindIndex(c =>
                EnterActionResolver.ConfirmKey(c.Kind, c.Title, c.Payload) == key);
        if (i >= 0)
        {
            _commands[i] = item with { Subtitle = "Press Enter again to confirm", Hint = "Confirm" };
            CommandsList.ItemsSource = null;
            CommandsList.ItemsSource = _commands;
            ApplySelection();
        }
        _armedKey = key;
        try { _armTimer?.Stop(); _armTimer?.Start(); } catch { }
    }

    private void DisarmConfirm()
    {
        if (_armedKey is null) return;
        _armedKey = null;
        try { _armTimer?.Stop(); } catch { }
        if (!_aiMode)
        {
            try { RefreshResults(SearchBox.Text ?? ""); } catch { }
        }
    }

    // ---- resize grip ----

    private void ResizeGrip_Pressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            e.Handled = true; // claim the press so the title-bar drag region ignores it
            _resizing = true;
            _resizeStart = e.GetCurrentPoint(ResizeGrip).Position;
            _resizeOrig = WindowHelpers.AppWindowOf(this).Size;
            ResizeGrip.CapturePointer(e.Pointer);
        }
        catch { _resizing = false; }
    }

    private void ResizeGrip_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        try
        {
            var cur = e.GetCurrentPoint(ResizeGrip).Position;
            int w = Math.Max(MinLauncherWidth, (int)(_resizeOrig.Width + (cur.X - _resizeStart.X)));
            int h = Math.Max(MinLauncherHeight, (int)(_resizeOrig.Height + (cur.Y - _resizeStart.Y)));
            WindowHelpers.AppWindowOf(this).Resize(new Windows.Graphics.SizeInt32(w, h));
        }
        catch { }
    }

    private void ResizeGrip_Released(object sender, PointerRoutedEventArgs e)
    {
        _resizing = false;
        try { ResizeGrip.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void MoveSelection(int delta)
    {
        if (TotalCount == 0) return;
        _sel = Math.Clamp(_sel + delta, 0, TotalCount - 1);
        ApplySelection();
    }

    // ---- AI view ----

    private async Task AskAiAsync(string question, bool followUp)
    {
        question = (question ?? "").Trim();
        if (string.IsNullOrEmpty(question)) return;

        var s = SettingsStore.Current;
        // New launcher query starts a fresh conversation; follow-ups continue it.
        // A blank view's conversation stays transient (not in _convos, never saved)
        // until its first message, so empty chats are never stored.
        bool needNew = _convo is null || (!followUp && _convos.Contains(_convo));
        if (needNew)
            _convo = ChatStore.Create(s.DefaultModel, question);
        if (_convo is null) return; // unreachable: needNew is true when _convo is null
        if (!_convos.Contains(_convo))
            _convos.Insert(0, _convo);
        while (_convos.Count > ChatStore.MaxConversations)
            _convos.RemoveAt(_convos.Count - 1);
        EnterAiMode(question);

        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            _aiRawAnswer = "No API key configured.\n\nOpen Settings (⌘ button below) and add your OpenAI-compatible endpoint, API key and model, then ask again.";
            ChatStore.Append(_convo, "user", question, s.DefaultModel);
            RenderConversation(streaming: _aiRawAnswer);
            SetSpinner(false);
            return;
        }

        SetSpinner(true);
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;

        try
        {
            _aiRawAnswer = "Thinking…";
            ChatStore.Append(_convo, "user", question, s.DefaultModel);
            RenderConversation(thinking: true);
            string answer;
            var llmHistory = ChatStore.ForLlm(_convo).SkipLast(1).ToList();
            try
            {
                answer = await _llm.AskStreamingAsync(s.Endpoint, s.ApiKey, s.DefaultModel,
                    s.Temperature, s.SystemPrompt, llmHistory, question,
                    partial => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        _aiRawAnswer = partial;
                        RenderConversation(streaming: partial);
                        ScrollAiToBottom();
                    }), ct);
            }
            catch (Exception) when (_aiRawAnswer == "Thinking…")
            {
                // Streaming not supported by this endpoint — fall back to plain request.
                answer = await _llm.AskAsync(s.Endpoint, s.ApiKey, s.DefaultModel,
                    s.Temperature, s.SystemPrompt, llmHistory, question, ct);
                _aiRawAnswer = answer;
                RenderConversation(streaming: answer);
                ScrollAiToBottom();
            }

            ChatStore.Append(_convo, "assistant", answer, s.DefaultModel);
            _aiRawAnswer = answer;
            ChatStore.Save(_convos);
            RefreshConvoPicker();
            RenderConversation();
            ScrollAiToBottom();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _aiRawAnswer = "Error: " + ex.Message;
            RenderConversation(streaming: _aiRawAnswer);
        }
        finally
        {
            SetSpinner(false);
            AiInput.Focus(FocusState.Programmatic);
        }
    }

    private void ShowAiChrome()
    {
        _aiMode = true;
        _aiCts?.Cancel();
        SearchHeaderGrid.Visibility = Visibility.Collapsed;
        TopDivider.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Collapsed;
        BottomDivider.Visibility = Visibility.Collapsed;
        FooterGrid.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Visible;
        // AI title row becomes the drag region (footer is hidden in this mode).
        try { SetTitleBar(AiTitleBarGrid); } catch { }
    }

    private void EnterAiMode(string question)
    {
        ShowAiChrome();

        var model = SettingsStore.Current.DefaultModel;
        AiTitle.Text = _convo?.Title ?? question;
        AiSubtitle.Text = model;
        AiQueryPill.Text = question;
        AiQueryPillBorder.Visibility = Visibility.Visible;
        AiInput.Text = string.Empty;
        SetSpinner(true);
        RefreshConvoPicker();
        AiInput.Focus(FocusState.Programmatic);
    }

    private void EnterBlankAiView()
    {
        ShowAiChrome();

        var model = SettingsStore.Current.DefaultModel;
        // Transient: not added to _convos until its first message is sent.
        _convo = ChatStore.Create(model, "");
        _convo.Title = "New chat";
        AiTitle.Text = "New chat";
        AiSubtitle.Text = model;
        AiQueryPillBorder.Visibility = Visibility.Collapsed;
        _aiRawAnswer = string.Empty;
        RenderAnswer("Ask anything below to start a new conversation.");
        SetSpinner(false);
        RefreshConvoPicker();
        AiInput.Focus(FocusState.Programmatic);
    }

    private void ExitAiMode()
    {
        _aiMode = false;
        _aiCts?.Cancel();
        try { ChatStore.Save(_convos); } catch { }
        SetSpinner(false);
        AiPanel.Visibility = Visibility.Collapsed;
        SearchHeaderGrid.Visibility = Visibility.Visible;
        TopDivider.Visibility = Visibility.Visible;
        ResultsScroll.Visibility = Visibility.Visible;
        BottomDivider.Visibility = Visibility.Visible;
        FooterGrid.Visibility = Visibility.Visible;
        try { SetTitleBar(FooterGrid); } catch { }
        RefreshResults(SearchBox.Text);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void SetSpinner(bool on)
    {
        AiSpinner.IsActive = on;
        AiSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollAiToBottom()
    {
        try { AiScroll.ChangeView(null, AiScroll.ScrollableHeight, null); } catch { }
    }

    private async void AiInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var q = AiInput.Text;
            AiInput.Text = string.Empty;
            await AskAiAsync(q, followUp: true);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ExitAiMode();
        }
    }

    private void AiBackButton_Click(object sender, RoutedEventArgs e) => ExitAiMode();

    private void AiNewButton_Click(object sender, RoutedEventArgs e)
    {
        try { ChatStore.Save(_convos); } catch { }
        EnterBlankAiView();
    }

    private void AiConvoBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (AiConvoBox.SelectedItem is quikbar.Models.ChatConversation c && !ReferenceEquals(c, _convo))
            {
                _convo = c;
                AiTitle.Text = c.Title;
                AiSubtitle.Text = string.IsNullOrEmpty(c.Model) ? SettingsStore.Current.DefaultModel : c.Model;
                AiQueryPillBorder.Visibility = Visibility.Collapsed;
                _aiRawAnswer = c.Messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
                RenderConversation();
                AiInput.Focus(FocusState.Programmatic);
            }
        }
        catch { }
    }

    private void RefreshConvoPicker()
    {
        try
        {
            if (AiConvoBox is null) return;
            AiConvoBox.SelectionChanged -= AiConvoBox_SelectionChanged;
            AiConvoBox.ItemsSource = null;
            AiConvoBox.ItemsSource = _convos;
            AiConvoBox.SelectedItem = _convo;
            AiConvoBox.SelectionChanged += AiConvoBox_SelectionChanged;
        }
        catch { }
    }

    private void RenderConversation(string? streaming = null, bool thinking = false)
    {
        AiAnswer.Blocks.Clear();
        var msgs = _convo?.Messages.Select(m => (m.Role, m.Content)).ToList()
            ?? new List<(string, string)>();
        foreach (var b in ConversationView.Build(msgs, streaming, thinking))
            RenderBubble(b.Role, b.Markdown);
    }

    private void RenderBubble(string role, string markdown)
    {
        var header = new Paragraph { Margin = new Thickness(0, 6, 0, 4) };
        header.Inlines.Add(new Run
        {
            Text = role == "user" ? "You" : "AI",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
        });
        AiAnswer.Blocks.Add(header);
        RenderMarkdownInto(markdown);
    }

    private void RenderMarkdownInto(string markdown) => RenderAnswer(markdown, clear: false);

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var q = AiInput.Text;
        AiInput.Text = string.Empty;
        await AskAiAsync(q, followUp: true);
    }

    private void AiPopoutButton_Click(object sender, RoutedEventArgs e) => CopyAnswer();

    private void CopyAnswer()
    {
        if (string.IsNullOrEmpty(_aiRawAnswer)) return;
        CopyToClipboard(_aiRawAnswer);
    }

    private void AiModelButton_Click(object sender, RoutedEventArgs e) => App.ShowSettings();

    private void CopyToClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch { }
    }

    // ---- markdown-lite rendering (bold, code, headers, bullets) ----

    private void RenderAnswer(string markdown, bool clear = true)
    {
        if (clear) AiAnswer.Blocks.Clear();
        if (string.IsNullOrEmpty(markdown)) return;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        Paragraph? para = null;

        void Flush()
        {
            if (para is not null && para.Inlines.Count > 0)
                AiAnswer.Blocks.Add(para);
            para = null;
        }

        Paragraph NewPara(double fontSize = 15)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            if (fontSize != 15) p.FontSize = fontSize;
            return p;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            if (line.StartsWith("### ")) { Flush(); var p = NewPara(16); AddInline(p, line[4..], true); AiAnswer.Blocks.Add(p); continue; }
            if (line.StartsWith("## ")) { Flush(); var p = NewPara(17); AddInline(p, line[3..], true); AiAnswer.Blocks.Add(p); continue; }
            if (line.StartsWith("# ")) { Flush(); var p = NewPara(18); AddInline(p, line[2..], true); AiAnswer.Blocks.Add(p); continue; }

            if (line.StartsWith("> ") && line.Length > 2)
            {
                Flush();
                var p = NewPara();
                if (Application.Current.Resources["TextFillColorSecondaryBrush"] is SolidColorBrush secondary)
                    p.Foreground = secondary;
                AddInline(p, line[2..], false);
                AiAnswer.Blocks.Add(p);
                continue;
            }

            if ((line.StartsWith("- ") || line.StartsWith("* ")) && line.Length > 2)
            {
                Flush();
                var p = NewPara();
                p.Inlines.Add(new Run { Text = "•   " });
                AddInline(p, line[2..], false);
                AiAnswer.Blocks.Add(p);
                continue;
            }

            var num = Regex.Match(line, @"^(\d+)[.)]\s+(.*)$");
            if (num.Success)
            {
                Flush();
                var p = NewPara();
                var n = new Run { Text = num.Groups[1].Value + ".   ", FontWeight = FontWeights.SemiBold };
                p.Inlines.Add(n);
                AddInline(p, num.Groups[2].Value, false);
                AiAnswer.Blocks.Add(p);
                continue;
            }

            para ??= NewPara();
            if (para.Inlines.Count > 0)
                para.Inlines.Add(new Run { Text = " " });
            AddInline(para, line, false);
        }
        Flush();
    }

    private static void AddInline(Paragraph p, string text, bool bold)
    {
        var plain = new StringBuilder();
        void FlushPlain(bool b)
        {
            if (plain.Length == 0) return;
            var r = new Run { Text = plain.ToString() };
            if (b) r.FontWeight = FontWeights.Bold;
            p.Inlines.Add(r);
            plain.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    FlushPlain(bold);
                    p.Inlines.Add(new Run { Text = text.Substring(i + 2, end - i - 2), FontWeight = FontWeights.Bold });
                    i = end + 2;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain(bold);
                    p.Inlines.Add(new Run
                    {
                        Text = text.Substring(i + 1, end - i - 1),
                        FontFamily = new FontFamily(AppFonts.GeistMono),
                    });
                    i = end + 1;
                    continue;
                }
            }
            plain.Append(text[i]);
            i++;
        }
        FlushPlain(bold);
    }
}
