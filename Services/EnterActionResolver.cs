namespace quikbar.Services;

/// <summary>
/// Pure decision table for what Enter/click does on a launcher row.
/// Mirrors MainWindow.RunItemAsync; kept UI-free so it is unit-testable.
/// </summary>
public enum EnterActionKind
{
    /// <summary>Open the AI view (launcher stays open).</summary>
    AskAi,
    /// <summary>Open settings (launcher hides itself first).</summary>
    ShowSettings,
    /// <summary>Copy text to clipboard, then hide.</summary>
    CopyAndHide,
    /// <summary>Fetch a dictionary definition, then refresh inline (stays open).</summary>
    FetchDictionaryAndRefresh,
    /// <summary>Run the row's ActionAsync, then hide.</summary>
    RunAndHide,
    /// <summary>First Enter on a destructive row: arm it, show "press again" (stays open).</summary>
    ConfirmRequired,
    /// <summary>Do nothing (e.g. "No definition found" row).</summary>
    None,
}

public sealed record EnterAction(EnterActionKind Kind, string? CopyText = null, string? Word = null);

public static class EnterActionResolver
{
    /// <summary>
    /// Stable key for an armable row: title+payload survive the arming
    /// rewrite (only subtitle/hint change), so the 2nd Enter still matches.
    /// </summary>
    public static string ConfirmKey(LauncherItemKind kind, string title, string? payload) =>
        $"{kind}|{title}|{payload}";

    public static EnterAction Resolve(LauncherItemKind kind, string title, string? payload, bool hasAction,
        bool requiresConfirmation = false, bool armed = false)
    {
        if (requiresConfirmation && !armed)
            return new EnterAction(EnterActionKind.ConfirmRequired);

        return kind switch
        {
            LauncherItemKind.AskAi => new EnterAction(EnterActionKind.AskAi),
            LauncherItemKind.Settings => new EnterAction(EnterActionKind.ShowSettings),
            LauncherItemKind.Calculator or LauncherItemKind.Currency =>
                string.IsNullOrEmpty(payload)
                    ? new EnterAction(EnterActionKind.None)
                    : new EnterAction(EnterActionKind.CopyAndHide, CopyText: payload),
            LauncherItemKind.Dictionary when !string.IsNullOrEmpty(payload) && title.StartsWith("Define")
                => new EnterAction(EnterActionKind.FetchDictionaryAndRefresh, Word: payload),
            LauncherItemKind.Dictionary when !string.IsNullOrEmpty(payload)
                => new EnterAction(EnterActionKind.CopyAndHide, CopyText: payload),
            LauncherItemKind.Dictionary => new EnterAction(EnterActionKind.None),
            _ => hasAction
                ? new EnterAction(EnterActionKind.RunAndHide)
                : new EnterAction(EnterActionKind.None),
        };
    }
}
