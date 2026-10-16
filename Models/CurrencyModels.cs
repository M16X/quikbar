namespace quikbar.Models;

public sealed class CurrencyQuery
{
    public double Amount { get; set; } = 1;
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class CurrencyCache
{
    public string Base { get; set; } = "USD";
    public Dictionary<string, double> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Date { get; set; } = "";
    public DateTime FetchedAt { get; set; } = DateTime.MinValue;
}
