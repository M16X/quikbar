using quikbar.Services;
using Xunit;

namespace quikbar.Tests;

public sealed class CurrencyParserTests
{
    [Theory]
    [InlineData("100 usd to eur", 100, "USD", "EUR")]
    [InlineData("100 dollars in euros", 100, "USD", "EUR")]
    [InlineData("$100 to €", 100, "USD", "EUR")]
    [InlineData("€100 to $", 100, "EUR", "USD")]
    [InlineData("usd to eur", 1, "USD", "EUR")]
    [InlineData("100usd eur", 100, "USD", "EUR")]
    [InlineData("250 pounds to yen", 250, "GBP", "JPY")]
    [InlineData("10 USD in INR", 10, "USD", "INR")]
    public void ParsesCommonForms(string input, double amount, string from, string to)
    {
        Assert.True(CurrencyParser.TryParse(input, out var q), input);
        Assert.Equal(amount, q.Amount);
        Assert.Equal(from, q.From);
        Assert.Equal(to, q.To);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("100 usd")]
    [InlineData("usd to usd")]
    [InlineData("")]
    [InlineData("notepad")]
    public void RejectsNonCurrency(string input)
    {
        Assert.False(CurrencyParser.TryParse(input, out _));
    }
}

public sealed class CurrencyServiceTests
{
    private static Models.CurrencyCache Cache() => new()
    {
        Base = "USD",
        Rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["EUR"] = 0.9, ["GBP"] = 0.8, ["JPY"] = 150,
        },
        Date = "2026-10-14",
        FetchedAt = DateTime.UtcNow,
    };

    [Fact]
    public void ConvertsViaCrossRate()
    {
        Assert.True(CurrencyService.TryConvert(100, "USD", "EUR", Cache(), out var v));
        Assert.Equal(90, v, precision: 6);
        // EUR -> GBP cross: 100 EUR = 100/0.9*0.8
        Assert.True(CurrencyService.TryConvert(100, "EUR", "GBP", Cache(), out var g));
        Assert.Equal(100 / 0.9 * 0.8, g, precision: 6);
    }

    [Fact]
    public void SameCurrencyIsIdentity()
    {
        Assert.True(CurrencyService.TryConvert(5, "USD", "USD", Cache(), out var v));
        Assert.Equal(5, v);
    }

    [Fact]
    public void UnknownCodeFails()
    {
        Assert.False(CurrencyService.TryConvert(5, "USD", "XXX", Cache(), out _));
        Assert.False(CurrencyService.TryConvert(5, "XXX", "USD", Cache(), out _));
    }

    [Fact]
    public void StaleLogic()
    {
        Assert.True(CurrencyService.IsStale(DateTime.MinValue, DateTime.UtcNow));
        Assert.False(CurrencyService.IsStale(DateTime.UtcNow, DateTime.UtcNow));
        Assert.True(CurrencyService.IsStale(DateTime.UtcNow.AddHours(-13), DateTime.UtcNow));
    }

    [Fact]
    public void CacheRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quikbar-fx-{Guid.NewGuid():N}.json");
        try
        {
            CurrencyService.SaveCache(Cache(), path);
            var loaded = CurrencyService.LoadCache(path);
            Assert.NotNull(loaded);
            Assert.Equal(0.9, loaded!.Rates["EUR"], precision: 9);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
