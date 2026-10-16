namespace quikbar.Models;

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

public sealed class ChatConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    public string Model { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
}

public sealed class DictionaryMeaning
{
    public string PartOfSpeech { get; set; } = "";
    public List<DictionaryDefinition> Definitions { get; set; } = new();
}

public sealed class DictionaryDefinition
{
    public string Definition { get; set; } = "";
    public string Example { get; set; } = "";
}

public sealed class DictionaryEntry
{
    public string Word { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public List<DictionaryMeaning> Meanings { get; set; } = new();

    public string TopDefinition =>
        Meanings.SelectMany(m => m.Definitions).Select(d => d.Definition).FirstOrDefault() ?? "";
}
