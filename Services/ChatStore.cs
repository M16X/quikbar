using System.Text.Json;
using quikbar.Models;

namespace quikbar.Services;

/// <summary>
/// Persisted multi-conversation AI chat. Single chats.json, atomic writes.
/// Pure logic (caps, titles, trimming) is unit-testable; path injectable.
/// </summary>
public static class ChatStore
{
    public const int MaxConversations = 50;
    public const int MaxMessagesPerConversation = 100;
    public const int LlmHistoryCap = 12;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar", "chats.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<ChatConversation> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            var list = JsonSerializer.Deserialize<List<ChatConversation>>(File.ReadAllText(path));
            // Drop empty chats (blank views that were never sent from).
            return (list ?? new()).Where(c => c.Messages.Count > 0).ToList();
        }
        catch { return new(); }
    }

    public static void Save(List<ChatConversation> convs, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var trimmed = convs
                .Where(c => c.Messages.Count > 0) // never persist empty chats
                .OrderByDescending(c => c.UpdatedAt)
                .Take(MaxConversations)
                .ToList();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(trimmed, Opts));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static ChatConversation Create(string model, string firstQuestion)
    {
        return new ChatConversation
        {
            Model = model,
            Title = AutoTitle(firstQuestion),
            UpdatedAt = DateTime.UtcNow,
        };
    }

    public static string AutoTitle(string q)
    {
        q = (q ?? "").Trim().Replace('\n', ' ').Replace('\r', ' ');
        while (q.Contains("  ")) q = q.Replace("  ", " ");
        if (q.Length == 0) return "New chat";
        return q.Length <= 40 ? q : q[..40] + "…";
    }

    public static void Append(ChatConversation c, string role, string content, string model)
    {
        c.Messages.Add(new ChatMessage { Role = role, Content = content });
        while (c.Messages.Count > MaxMessagesPerConversation)
            c.Messages.RemoveAt(0);
        c.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(model)) c.Model = model;
        if ((c.Title == "New chat" || string.IsNullOrWhiteSpace(c.Title)) && role == "user")
            c.Title = AutoTitle(content);
    }

    /// <summary>Last N messages in LLM (role,content) tuple shape.</summary>
    public static List<(string Role, string Content)> ForLlm(ChatConversation c, int cap = LlmHistoryCap)
    {
        return c.Messages.TakeLast(cap).Select(m => (m.Role, m.Content)).ToList();
    }
}
