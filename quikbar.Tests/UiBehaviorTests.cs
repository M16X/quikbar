using quikbar.Models;
using quikbar.Services;
using Xunit;

namespace quikbar.Tests;

/// <summary>
/// UI behaviour tests: they assert exactly what the launcher displays
/// (rows, order, placeholders, chat bubbles) and what Enter does,
/// through the same helpers MainWindow calls. No windows are created.
/// </summary>
public sealed class LauncherDisplayTests : IDisposable
{
    public void Dispose() => LauncherCommands.ClearTestHooks();

    private static CurrencyCache Fx() => new()
    {
        Base = "USD",
        Rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["EUR"] = 0.9, ["GBP"] = 0.8,
        },
        Date = "2026-10-14",
        FetchedAt = DateTime.UtcNow,
    };

    private static Dictionary<string, List<DictionaryEntry>> Dict() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["serendipity"] = new()
        {
            new DictionaryEntry
            {
                Word = "serendipity",
                Phonetic = "/ˌser.ənˈdɪp.ə.ti/",
                Meanings = new()
                {
                    new DictionaryMeaning
                    {
                        PartOfSpeech = "noun",
                        Definitions = new()
                        {
                            new DictionaryDefinition { Definition = "Finding happy things by chance." },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public void EmptyQuery_ShowsAppsAndCommands()
    {
        var (sug, cmd) = LauncherCommands.QueryGrouped("");
        Assert.Contains(cmd, c => c.Kind == LauncherItemKind.Settings);
        Assert.NotEmpty(sug); // Start-menu apps on Windows
    }

    [Fact]
    public void CalculatorRow_IsFirst_WithCopyPayload()
    {
        var (_, cmd) = LauncherCommands.QueryGrouped("2+2*3");
        Assert.Equal(LauncherItemKind.Calculator, cmd[0].Kind);
        Assert.Equal("= 8", cmd[0].Title);
        Assert.Equal("8", cmd[0].Payload);
    }

    [Fact]
    public void CurrencyRow_IsFirst_WhenRatesCached()
    {
        LauncherCommands.SetFxCacheForTests(Fx());
        var (_, cmd) = LauncherCommands.QueryGrouped("100 usd to eur");
        Assert.Equal(LauncherItemKind.Currency, cmd[0].Kind);
        Assert.Equal("= 90 EUR", cmd[0].Title);
        Assert.Equal("90 EUR", cmd[0].Payload);
        Assert.Contains("1 USD = 0.9 EUR", cmd[0].Subtitle);
    }

    [Fact]
    public void DictionaryPrefix_ShowsCachedDefinitions_NotForBareWord()
    {
        LauncherCommands.SetDictEntriesForTests(Dict());
        var (_, withPrefix) = LauncherCommands.QueryGrouped("define serendipity");
        var defs = withPrefix.Where(c => c.Kind == LauncherItemKind.Dictionary).ToList();
        Assert.NotEmpty(defs);
        Assert.Contains("Finding happy things by chance.", defs[0].Subtitle);

        var (_, bare) = LauncherCommands.QueryGrouped("serendipity");
        Assert.DoesNotContain(bare, c => c.Kind == LauncherItemKind.Dictionary);
    }

    [Fact]
    public void AskAiEntry_AlwaysPresent()
    {
        foreach (var q in new[] { "2+2", "100 usd to eur", "define serendipity", "notepad" })
        {
            LauncherCommands.SetFxCacheForTests(Fx());
            LauncherCommands.SetDictEntriesForTests(Dict());
            var (_, cmd) = LauncherCommands.QueryGrouped(q);
            Assert.Contains(cmd, c => c.Kind == LauncherItemKind.AskAi);
        }
    }

    [Fact]
    public void Placeholders_DictionaryLookupRow_WhenNeverFetched()
    {
        LauncherCommands.SetDictEntriesForTests(new(StringComparer.OrdinalIgnoreCase));
        var (_, cmd) = LauncherCommands.QueryGrouped("define quizzaciously");
        var ph = LauncherCommands.PlaceholderRowsFor("define quizzaciously", cmd);
        var row = Assert.Single(ph);
        Assert.Equal(LauncherItemKind.Dictionary, row.Kind);
        Assert.Equal("quizzaciously", row.Payload);
        Assert.StartsWith("Define", row.Title);
    }

    [Fact]
    public void Placeholders_NoneWhenCached()
    {
        LauncherCommands.SetFxCacheForTests(Fx());
        LauncherCommands.SetDictEntriesForTests(Dict());
        var (_, cmd1) = LauncherCommands.QueryGrouped("100 usd to eur");
        Assert.Empty(LauncherCommands.PlaceholderRowsFor("100 usd to eur", cmd1));
        var (_, cmd2) = LauncherCommands.QueryGrouped("define serendipity");
        Assert.Empty(LauncherCommands.PlaceholderRowsFor("define serendipity", cmd2));
    }

    [Fact]
    public void Placeholders_ConvertingRow_WhenNoRates()
    {
        LauncherCommands.SetFxCacheForTests(null); // + no file on test machines w/o app ever run
        if (CurrencyService.LoadCache() is not null) return; // real cache exists: skip
        var (_, cmd) = LauncherCommands.QueryGrouped("100 usd to eur");
        Assert.DoesNotContain(cmd, c => c.Kind == LauncherItemKind.Currency);
        var ph = LauncherCommands.PlaceholderRowsFor("100 usd to eur", cmd);
        var row = Assert.Single(ph);
        Assert.Equal("Converting…", row.Title);
        // Enter on a loading row must not copy/hide (see EnterAction tests).
        var a = EnterActionResolver.Resolve(row.Kind, row.Title, row.Payload, false);
        Assert.Equal(EnterActionKind.None, a.Kind);
    }
}

public sealed class ConversationViewTests
{
    [Fact]
    public void Empty_Thinking_ShowsThinkingBubble()
    {
        var b = ConversationView.Build(new List<(string, string)>(), thinking: true);
        var bubble = Assert.Single(b);
        Assert.Equal("assistant", bubble.Role);
        Assert.Equal("Thinking…", bubble.Markdown);
    }

    [Fact]
    public void Empty_Idle_ShowsNothing()
    {
        Assert.Empty(ConversationView.Build(new List<(string, string)>()));
    }

    [Fact]
    public void Streaming_AppendsAssistantBubble_AfterUser()
    {
        var msgs = new List<(string, string)> { ("user", "hi") };
        var b = ConversationView.Build(msgs, streaming: "hello");
        Assert.Equal(2, b.Count);
        Assert.Equal(("user", "hi"), (b[0].Role, b[0].Markdown));
        Assert.Equal(("assistant", "hello"), (b[1].Role, b[1].Markdown));
    }

    [Fact]
    public void Streaming_NeverReordersHistory()
    {
        // Follow-up turn: partial answer for the newest question goes last,
        // earlier turns are untouched.
        var msgs = new List<(string, string)>
        {
            ("user", "q1"), ("assistant", "old"), ("user", "q2"),
        };
        var b = ConversationView.Build(msgs, streaming: "new");
        Assert.Equal(4, b.Count);
        Assert.Equal("old", b[1].Markdown);
        Assert.Equal(("assistant", "new"), (b[3].Role, b[3].Markdown));
    }

    [Fact]
    public void Streaming_Replaces_TrailingAssistantBubble()
    {
        // Regeneration case: streaming updates the in-progress answer in place.
        var msgs = new List<(string, string)> { ("user", "q1"), ("assistant", "partial") };
        var b = ConversationView.Build(msgs, streaming: "full answer");
        Assert.Equal(2, b.Count);
        Assert.Equal("full answer", b[1].Markdown);
    }

    [Fact]
    public void Thinking_AppendsAfterHistory()
    {
        var msgs = new List<(string, string)> { ("user", "q"), ("assistant", "a") };
        var b = ConversationView.Build(msgs, thinking: true);
        Assert.Equal(3, b.Count);
        Assert.Equal("Thinking…", b[2].Markdown);
    }

    [Fact]
    public void NoKeyError_IsVisible_AsAIBubble()
    {
        // Locks the fix: the "No API key" text must render, not vanish.
        var msgs = new List<(string, string)> { ("user", "hello") };
        var b = ConversationView.Build(msgs, streaming: "No API key configured.");
        Assert.Equal(2, b.Count);
        Assert.Equal("No API key configured.", b[1].Markdown);
    }

    [Fact]
    public void TurnOrder_Preserved()
    {
        var msgs = new List<(string, string)>
        {
            ("user", "q1"), ("assistant", "a1"), ("user", "q2"), ("assistant", "a2"),
        };
        var b = ConversationView.Build(msgs);
        Assert.Equal(new[] { "q1", "a1", "q2", "a2" }, b.Select(x => x.Markdown));
    }
}

public sealed class EnterActionTests
{
    [Fact]
    public void AskAi_OpensAIView() =>
        Assert.Equal(EnterActionKind.AskAi,
            EnterActionResolver.Resolve(LauncherItemKind.AskAi, "Ask AI: x", "x", false).Kind);

    [Fact]
    public void Settings_OpensSettings() =>
        Assert.Equal(EnterActionKind.ShowSettings,
            EnterActionResolver.Resolve(LauncherItemKind.Settings, "Settings", null, false).Kind);

    [Theory]
    [InlineData(LauncherItemKind.Calculator, "= 4", "4")]
    [InlineData(LauncherItemKind.Currency, "= 90 EUR", "90 EUR")]
    public void CalculatorAndCurrency_CopyAndHide(LauncherItemKind kind, string title, string payload)
    {
        var a = EnterActionResolver.Resolve(kind, title, payload, false);
        Assert.Equal(EnterActionKind.CopyAndHide, a.Kind);
        Assert.Equal(payload, a.CopyText);
    }

    [Fact]
    public void DictionaryPlaceholder_FetchesInline() {
        var a = EnterActionResolver.Resolve(
            LauncherItemKind.Dictionary, "Define “serendipity”…", "serendipity", false);
        Assert.Equal(EnterActionKind.FetchDictionaryAndRefresh, a.Kind);
        Assert.Equal("serendipity", a.Word);
    }

    [Fact]
    public void DictionaryDefinition_CopiesAndHides()
    {
        var a = EnterActionResolver.Resolve(
            LauncherItemKind.Dictionary, "serendipity /…/ · noun", "Finding happy things.", false);
        Assert.Equal(EnterActionKind.CopyAndHide, a.Kind);
        Assert.Equal("Finding happy things.", a.CopyText);
    }

    [Fact]
    public void DictionaryNotFound_DoesNothing()
    {
        var a = EnterActionResolver.Resolve(
            LauncherItemKind.Dictionary, "No definition found for “xyz”", "", false);
        Assert.Equal(EnterActionKind.None, a.Kind);
    }

    [Fact]
    public void AppRow_RunsAndHides()
    {
        var a = EnterActionResolver.Resolve(LauncherItemKind.App, "Notepad", null, hasAction: true);
        Assert.Equal(EnterActionKind.RunAndHide, a.Kind);
    }
}

public sealed class ConfirmTests
{
    [Theory]
    [InlineData("Lock PC")]
    [InlineData("Sleep")]
    [InlineData("Shutdown")]
    [InlineData("Restart")]
    [InlineData("Sign out")]
    [InlineData("Empty Recycle Bin")]
    public void DestructiveCommands_RequireConfirmation(string title)
    {
        var cmd = LauncherCommands.BuiltInCommands().First(c => c.Title == title);
        Assert.True(cmd.RequiresConfirmation);
    }

    [Theory]
    [InlineData("Task Manager")]
    [InlineData("Notepad")]
    [InlineData("Settings")]
    [InlineData("Take Screenshot (Snipping Tool)")]
    public void SafeCommands_NoConfirmation(string title)
    {
        var cmd = LauncherCommands.BuiltInCommands().First(c => c.Title == title);
        Assert.False(cmd.RequiresConfirmation);
    }

    [Fact]
    public void UnarmedDestructive_ResolvesConfirmRequired()
    {
        var a = EnterActionResolver.Resolve(LauncherItemKind.Command, "Shutdown", null,
            hasAction: true, requiresConfirmation: true, armed: false);
        Assert.Equal(EnterActionKind.ConfirmRequired, a.Kind);
    }

    [Fact]
    public void ArmedDestructive_ProceedsToRun()
    {
        var a = EnterActionResolver.Resolve(LauncherItemKind.Command, "Shutdown", null,
            hasAction: true, requiresConfirmation: true, armed: true);
        Assert.Equal(EnterActionKind.RunAndHide, a.Kind);
    }

    [Fact]
    public void ArmedKey_SurvivesArmingRewrite()
    {
        // The 2nd Enter hits the rewritten row (new subtitle/hint) and must still match.
        var item = LauncherCommands.BuiltInCommands().First(c => c.Title == "Shutdown");
        var before = EnterActionResolver.ConfirmKey(item.Kind, item.Title, item.Payload);
        var armedRow = item with { Subtitle = "Press Enter again to confirm", Hint = "Confirm" };
        var after = EnterActionResolver.ConfirmKey(armedRow.Kind, armedRow.Title, armedRow.Payload);
        Assert.Equal(before, after);

        var a = EnterActionResolver.Resolve(armedRow.Kind, armedRow.Title, armedRow.Payload,
            hasAction: true, requiresConfirmation: true, armed: true);
        Assert.Equal(EnterActionKind.RunAndHide, a.Kind);
    }

    [Fact]
    public void SafeRows_UnaffectedByConfirmation()
    {
        var a = EnterActionResolver.Resolve(LauncherItemKind.Calculator, "= 4", "4",
            hasAction: false, requiresConfirmation: false, armed: false);
        Assert.Equal(EnterActionKind.CopyAndHide, a.Kind);
    }
}
