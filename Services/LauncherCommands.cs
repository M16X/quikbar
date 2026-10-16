using System.Diagnostics;
using quikbar.Models;

namespace quikbar.Services;

public enum LauncherItemKind
{
    App,
    Command,
    Web,
    Calculator,
    Currency,
    Dictionary,
    AskAi,
    Settings,
}

public sealed record LauncherItem(
    LauncherItemKind Kind,
    string Title,
    string Subtitle,
    string Glyph,
    Func<Task>? ActionAsync = null,
    string? Payload = null,
    string Hint = "",
    bool RequiresConfirmation = false);

public static class LauncherCommands
{
    private static List<(string Name, string Path)>? _appCache;
    private static DateTime _appCacheTime;

    public static IReadOnlyList<LauncherItem> BuiltInCommands() => new List<LauncherItem>
    {
        new(LauncherItemKind.Settings, "Settings", "quikbar", "\uE713", Hint: "Command"),
        new(LauncherItemKind.Command, "Lock PC", "System", "\uE72E", () => RunAsync("rundll32.exe", "user32.dll,LockWorkStation"), Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Sleep", "System", "\uE7E8", () => RunAsync("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"), Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Shutdown", "System", "\uE7E8", () => RunAsync("shutdown", "/s /t 5"), Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Restart", "System", "\uE72C", () => RunAsync("shutdown", "/r /t 5"), Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Sign out", "System", "\uE76C", () => RunAsync("shutdown", "/l"), Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Empty Recycle Bin", "System", "\uE74D", EmptyRecycleBinAsync, Hint: "Command", RequiresConfirmation: true),
        new(LauncherItemKind.Command, "Task Manager", "System", "\uE890", () => RunAsync("taskmgr.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "Take Screenshot (Snipping Tool)", "System", "\uE722", () => RunAsync("snippingtool.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "Open Terminal", "System", "\uE81C", () => RunAsync("wt.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "Notepad", "System", "\uE70F", () => RunAsync("notepad.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "Calculator", "System", "\uE8EF", () => RunAsync("calc.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "File Explorer", "System", "\uE8B7", () => RunAsync("explorer.exe", ""), Hint: "Command"),
        new(LauncherItemKind.Command, "Control Panel", "System", "\uE713", () => RunAsync("control.exe", ""), Hint: "Command"),
    };

    public static (List<LauncherItem> Suggestions, List<LauncherItem> Commands) QueryGrouped(string text)
    {
        text = (text ?? "").Trim();
        var sug = new List<LauncherItem>();
        var cmd = new List<LauncherItem>();

        if (string.IsNullOrEmpty(text))
        {
            sug.AddRange(TopApps(5));
            cmd.AddRange(BuiltInCommands().Take(4));
            return (sug, cmd);
        }

        // Calculator: quick expression (safe parser, no DataTable)
        if (CalculatorService.TryEvaluate(text, out var calcDisplay, out var calcCopy))
            cmd.Add(new LauncherItem(LauncherItemKind.Calculator, $"= {calcDisplay}", text, "\uE8EF", Payload: calcCopy, Hint: "Calculator"));

        // Currency: natural-language conversion from cache (async refresh in MainWindow)
        if (TryCurrencyCached(text, out var currencyItem) && currencyItem is not null)
            cmd.Add(currencyItem);

        // Dictionary: explicit prefix + cached definitions (async fetch in MainWindow)
        foreach (var d in TryDictionaryCached(text))
            cmd.Add(d);

        var lower = text.ToLowerInvariant();

        // "ai " prefix -> boost AI; otherwise always include Ask AI entry
        var aiQuery = text.StartsWith("ai ", StringComparison.OrdinalIgnoreCase) ? text[3..].Trim()
            : text.StartsWith("ask ", StringComparison.OrdinalIgnoreCase) ? text[4..].Trim() : text;
        var aiTitle = string.IsNullOrEmpty(aiQuery) ? "Ask AI…" : $"Ask AI: {Truncate(aiQuery, 60)}";

        // Built-in commands fuzzy match
        foreach (var c in BuiltInCommands())
        {
            if (FuzzyMatch(c.Title, text))
                cmd.Add(c);
        }

        // Installed apps go to Suggestions
        foreach (var app in SearchApps(text).Take(5))
            sug.Add(new LauncherItem(LauncherItemKind.App, app.Name, "", "\uE71C",
                CreateAppAction(app.Path), Hint: "App"));

        // Web searches
        if (text.Length >= 2)
        {
            cmd.Add(new LauncherItem(LauncherItemKind.Web, $"Search Google for \"{Truncate(text, 50)}\"", "google.com", "\uE774",
                () => OpenUrlAsync("https://www.google.com/search?q=" + Uri.EscapeDataString(text)), Hint: "Command"));
            if (!text.Contains(' ') || lower.StartsWith("gh ") || lower.StartsWith("github "))
                cmd.Add(new LauncherItem(LauncherItemKind.Web, $"Search GitHub for \"{Truncate(text, 50)}\"", "github.com", "\uE774",
                    () => OpenUrlAsync("https://github.com/search?q=" + Uri.EscapeDataString(text)), Hint: "Command"));
            cmd.Add(new LauncherItem(LauncherItemKind.Web, $"Search YouTube for \"{Truncate(text, 50)}\"", "youtube.com", "\uE774",
                () => OpenUrlAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text)), Hint: "Command"));
        }

        // Direct URL
        if ((lower.StartsWith("http://") || lower.StartsWith("https://") || lower.EndsWith(".com") || lower.EndsWith(".dev") || lower.EndsWith(".io")) && !text.Contains(' '))
        {
            var url = lower.StartsWith("http") ? text : "https://" + text;
            cmd.Insert(0, new LauncherItem(LauncherItemKind.Web, $"Open {url}", "Browser", "\uE8A7",
                () => OpenUrlAsync(url), Hint: "Command"));
        }

        // AI entry near top of Commands (but keep Calculator/Currency/Dictionary first)
        cmd.Insert(Math.Min(2, cmd.Count),
            new LauncherItem(LauncherItemKind.AskAi, aiTitle, SettingsStore.Current.DefaultModel, "\uE8F2", Payload: aiQuery, Hint: "AI Command"));

        // Simple-first ranking: calculator, currency, dictionary above web/AI noise.
        cmd.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));

        return (sug, cmd.Take(9).ToList());
    }

    private static int Rank(LauncherItemKind k) => k switch
    {
        LauncherItemKind.Calculator => 0,
        LauncherItemKind.Currency => 1,
        LauncherItemKind.Dictionary => 2,
        LauncherItemKind.AskAi => 5,
        LauncherItemKind.Web => 6,
        _ => 3,
    };

    public static IEnumerable<LauncherItem> TopApps(int count)
    {
        EnsureAppCache();
        if (_appCache is null) yield break;
        foreach (var app in _appCache.Take(count))
            yield return new LauncherItem(LauncherItemKind.App, app.Name, "", "\uE71C",
                CreateAppAction(app.Path), Hint: "App");
    }

    private static Func<Task> CreateAppAction(string path) => () =>
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        return Task.CompletedTask;
    };

    private static Task RunAsync(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { }
        return Task.CompletedTask;
    }

    private static Task OpenUrlAsync(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        return Task.CompletedTask;
    }

    private static Task EmptyRecycleBinAsync()
    {
        try { Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"Clear-RecycleBin -Force\"") { UseShellExecute = true, CreateNoWindow = false }); } catch { }
        return Task.CompletedTask;
    }

    public static bool TryEvaluate(string expr, out string result)
    {
        if (CalculatorService.TryEvaluate(expr, out _, out var copy))
        {
            result = copy;
            return true;
        }
        result = "";
        return false;
    }

    // ---- currency (cached sync fast-path) ----

    private static CurrencyCache? _fxCache;
    private static DateTime _fxCacheTime;

    public static void SetFxCacheForTests(CurrencyCache? c)
    {
        _fxCache = c;
        _fxCacheTime = DateTime.UtcNow;
    }

    private static CurrencyCache? FxCache()
    {
        if (_fxCache is not null && (DateTime.UtcNow - _fxCacheTime).TotalMinutes < 30)
            return _fxCache;
        _fxCache = CurrencyService.LoadCache();
        _fxCacheTime = DateTime.UtcNow;
        return _fxCache;
    }

    public static void NoteFxRefreshed(CurrencyCache c)
    {
        _fxCache = c;
        _fxCacheTime = DateTime.UtcNow;
    }

    public static bool TryCurrencyCached(string text, out LauncherItem? item)
    {
        item = null;
        try
        {
            if (!CurrencyParser.TryParse(text, out var q)) return false;
            var cache = FxCache();
            if (cache is null) return false;
            if (!CurrencyService.TryConvert(q.Amount, q.From, q.To, cache, out var v))
                return false;
            var unit = CurrencyService.UnitRate(q.From, q.To, cache);
            var stale = CurrencyService.IsStale(cache.FetchedAt, DateTime.UtcNow) ? " · offline" : "";
            var date = string.IsNullOrEmpty(cache.Date) ? "" : $" · {cache.Date}";
            var amtStr = q.Amount % 1 == 0
                ? q.Amount.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture)
                : q.Amount.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            item = new LauncherItem(LauncherItemKind.Currency,
                $"= {CurrencyService.Format(v, q.To)} {q.To}",
                $"{amtStr} {q.From} = {CurrencyService.Format(v, q.To)} {q.To} · 1 {q.From} = {CurrencyService.Format(unit, q.To)} {q.To}{date}{stale}",
                "\uE825",
                Payload: $"{CurrencyService.Format(v, q.To)} {q.To}",
                Hint: "Currency");
            return true;
        }
        catch { return false; }
    }

    // ---- dictionary (cached sync fast-path) ----

    // In-memory override for tests so they never touch %AppData%.
    private static Dictionary<string, List<DictionaryEntry>>? _dictTestHook;

    public static void SetDictEntriesForTests(Dictionary<string, List<DictionaryEntry>>? entries) =>
        _dictTestHook = entries;

    public static void ClearTestHooks()
    {
        _fxCache = null;
        _dictTestHook = null;
    }

    private static bool TryDictLookup(string word, out List<DictionaryEntry>? entries)
    {
        if (_dictTestHook is not null)
            return _dictTestHook.TryGetValue(word, out entries);
        var cache = DictionaryService.LoadCache();
        if (cache.TryGetValue(word, out var hit))
        {
            entries = hit.Entries;
            return true;
        }
        entries = null;
        return false;
    }

    public static List<LauncherItem> TryDictionaryCached(string text)
    {
        var items = new List<LauncherItem>();
        try
        {
            if (!DictionaryService.TryParseQuery(text, out var word)) return items;
            if (!TryDictLookup(word, out var list)) return items;
            var entry = list?.FirstOrDefault();
            if (entry is null) return items;
            int n = 0;
            foreach (var meaning in entry.Meanings)
            {
                foreach (var d in meaning.Definitions)
                {
                    n++;
                    var title = n == 1 && !string.IsNullOrEmpty(entry.Phonetic)
                        ? $"{entry.Word} {entry.Phonetic} · {meaning.PartOfSpeech}"
                        : $"{entry.Word} · {meaning.PartOfSpeech} ({n})";
                    items.Add(new LauncherItem(LauncherItemKind.Dictionary, title,
                        Truncate(d.Definition, 90), "\uE8A5",
                        Payload: d.Definition, Hint: "Dictionary"));
                    if (items.Count >= 3) return items;
                }
            }
        }
        catch { }
        return items;
    }

    /// <summary>
    /// Placeholder rows for lookups with no cached value yet (currency rates
    /// missing, dictionary word never fetched). Extracted from MainWindow so the
    /// exact rows the UI shows are unit-testable. Callers insert these at index 0.
    /// </summary>
    public static List<LauncherItem> PlaceholderRowsFor(string text, IEnumerable<LauncherItem> current)
    {
        var out_ = new List<LauncherItem>();
        var kinds = new HashSet<LauncherItemKind>(current.Select(c => c.Kind));
        if (!kinds.Contains(LauncherItemKind.Dictionary)
            && DictionaryService.TryParseQuery(text, out var w))
        {
            out_.Add(new LauncherItem(LauncherItemKind.Dictionary,
                $"Define “{w}”…", "Dictionary — Enter to look up", "\uE8A5",
                Payload: w, Hint: "Dictionary"));
        }
        if (!kinds.Contains(LauncherItemKind.Currency)
            && CurrencyParser.TryParse(text, out var q)
            && FxCache() is null)
        {
            out_.Add(new LauncherItem(LauncherItemKind.Currency,
                "Converting…", $"{q.Amount} {q.From} → {q.To} — fetching rates", "\uE825",
                Payload: "", Hint: "Currency"));
        }
        return out_;
    }

    private static bool FuzzyMatch(string haystack, string needle)
    {
        haystack = haystack.ToLowerInvariant();
        needle = needle.ToLowerInvariant().Trim();
        if (haystack.Contains(needle)) return true;
        // subsequence match
        int hi = 0;
        foreach (var ch in needle)
        {
            if (ch == ' ') continue;
            hi = haystack.IndexOf(ch, hi);
            if (hi < 0) return false;
            hi++;
        }
        return true;
    }

    public static IEnumerable<(string Name, string Path)> SearchApps(string query)
    {
        EnsureAppCache();
        if (_appCache is null) yield break;
        foreach (var app in _appCache)
        {
            if (FuzzyMatch(app.Name, query))
                yield return app;
        }
    }

    private static void EnsureAppCache()
    {
        if (_appCache is not null && (DateTime.Now - _appCacheTime).TotalMinutes < 5) return;
        var list = new List<(string, string)>();
        try
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var lnk in Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories).Take(400))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                    if (seen.Add(name)) list.Add((name, lnk));
                }
            }
            // common exes on PATH-ish locations
            foreach (var exe in new[] { "code", "notepad", "mspaint", "calc", "wt", "explorer", "cmd", "powershell" })
                if (seen.Add(exe)) list.Add((exe, exe + ".exe"));
        }
        catch { }
        _appCache = list.OrderBy(x => x.Item1).ToList();
        _appCacheTime = DateTime.Now;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
