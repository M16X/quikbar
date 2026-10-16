using quikbar.Models;
using quikbar.Services;
using Xunit;

namespace quikbar.Tests;

public sealed class ChatStoreTests
{
    [Fact]
    public void AutoTitleTruncates()
    {
        Assert.Equal("New chat", ChatStore.AutoTitle(""));
        Assert.Equal("hello", ChatStore.AutoTitle("  hello  "));
        var long_ = new string('a', 100);
        Assert.Equal(41, ChatStore.AutoTitle(long_).Length); // 40 + …
    }

    [Fact]
    public void AppendCapsMessagesAndSetsTitle()
    {
        var c = ChatStore.Create("gpt-4o-mini", "what is 2+2?");
        Assert.Equal("what is 2+2?", c.Title);
        for (int i = 0; i < 120; i++)
            ChatStore.Append(c, "user", $"q{i}", "gpt-4o-mini");
        Assert.Equal(ChatStore.MaxMessagesPerConversation, c.Messages.Count);
    }

    [Fact]
    public void ForLlmTakesLast12()
    {
        var c = ChatStore.Create("m", "hi");
        for (int i = 0; i < 20; i++)
            ChatStore.Append(c, i % 2 == 0 ? "user" : "assistant", $"m{i}", "m");
        var h = ChatStore.ForLlm(c);
        Assert.Equal(12, h.Count);
        Assert.Equal("m19", h[^1].Content);
    }

    [Fact]
    public void SaveLoadRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quikbar-chats-{Guid.NewGuid():N}.json");
        try
        {
            var list = new List<ChatConversation>
            {
                ChatStore.Create("m", "first question"),
                ChatStore.Create("m", "second"),
            };
            ChatStore.Append(list[0], "user", "first question", "m");
            ChatStore.Append(list[1], "user", "second", "m");
            ChatStore.Save(list, path);
            var loaded = ChatStore.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new[] { "first question", "second" },
                loaded.Select(c => c.Messages.Last().Content).OrderBy(x => x));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Save_NeverPersistsEmptyChats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quikbar-chats-{Guid.NewGuid():N}.json");
        try
        {
            var empty = ChatStore.Create("m", ""); // blank view, never sent from
            empty.Title = "New chat";
            var full = ChatStore.Create("m", "hello");
            ChatStore.Append(full, "user", "hello", "m");
            ChatStore.Save(new() { empty, full }, path);
            var loaded = ChatStore.Load(path);
            var single = Assert.Single(loaded);
            Assert.Equal("hello", single.Messages.Last().Content);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_DropsEmptyChats_FromOlderBuilds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quikbar-chats-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"Id":"a","Title":"New chat","Model":"m","UpdatedAt":"2026-01-01T00:00:00Z","Messages":[]}]""");
            Assert.Empty(ChatStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

public sealed class AppFontsTests
{
    [Theory]
    [InlineData("Geist", "Geist-Variable.ttf")]
    [InlineData("Geist Mono", "GeistMono-Variable.ttf")]
    public void BundledFamilies_ReferenceVariableFiles(string family, string file)
    {
        var uri = family == "Geist" ? AppFonts.Geist : AppFonts.GeistMono;
        Assert.StartsWith("ms-appx:///Assets/Fonts/", uri);
        Assert.EndsWith(file + "#" + family, uri);
    }
}

public sealed class DictionaryServiceTests
{
    [Theory]
    [InlineData("define serendipity", "serendipity")]
    [InlineData("def apple", "apple")]
    [InlineData("meaning of life", "life")]
    [InlineData("define big apple", null)]
    [InlineData("dict banana", "banana")]
    [InlineData("notepad", null)]
    [InlineData("serendipity", null)]
    public void PrefixParsing(string input, string? expected)
    {
        var ok = DictionaryService.TryParseQuery(input, out var word);
        if (expected is null) Assert.False(ok);
        else { Assert.True(ok); Assert.Equal(expected, word); }
    }

    [Fact]
    public void ParsesDictionaryApiJson()
    {
        const string json = """
        [{"word":"hello","phonetic":"həˈləʊ","meanings":[
          {"partOfSpeech":"noun","definitions":[{"definition":"A greeting.","example":"She said hello."}]},
          {"partOfSpeech":"verb","definitions":[{"definition":"To greet."}]}
        ]}]
        """;
        var entries = DictionaryService.ParseJson(json);
        Assert.Single(entries);
        Assert.Equal("hello", entries[0].Word);
        Assert.Equal(2, entries[0].Meanings.Count);
        Assert.Equal("A greeting.", entries[0].TopDefinition);
    }

    [Fact]
    public void ParseJsonIgnoresErrors()
    {
        Assert.Empty(DictionaryService.ParseJson("""{"title":"No Definitions Found"}"""));
    }
}
