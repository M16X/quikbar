namespace quikbar.Services;

/// <summary>
/// Pure view-model for the AI chat answer pane: decides which You/AI bubbles
/// the launcher must render for a conversation plus in-flight streaming text.
/// Mirrors MainWindow.RenderConversation; kept UI-free so it is unit-testable.
/// </summary>
public sealed record DisplayBubble(string Role, string Markdown);

public static class ConversationView
{
    public const string ThinkingText = "Thinking…";

    public static List<DisplayBubble> Build(
        IReadOnlyList<(string Role, string Content)> messages,
        string? streaming = null,
        bool thinking = false)
    {
        // Streaming text is always the answer to the LAST user message
        // (every caller appends the user turn first), so it goes at the end:
        // appended after a trailing user bubble, or replacing a trailing
        // assistant bubble when regenerating. Never reorders history.
        var out_ = messages
            .Select(m => new DisplayBubble(m.Role, m.Content))
            .ToList();

        if (thinking)
        {
            out_.Add(new DisplayBubble("assistant", ThinkingText));
        }
        else if (streaming is not null)
        {
            if (out_.Count > 0 && out_[^1].Role == "assistant")
                out_[^1] = new DisplayBubble("assistant", streaming);
            else
                out_.Add(new DisplayBubble("assistant", streaming));
        }

        return out_;
    }
}
