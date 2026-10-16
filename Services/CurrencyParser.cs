using System.Globalization;
using System.Text.RegularExpressions;
using quikbar.Models;

namespace quikbar.Services;

/// <summary>
/// Natural-language currency query parsing. Pure, no I/O.
/// Handles: "100 usd to eur", "100 dollars in euros", "$100 to €",
/// "usd to eur" (=1), "100usd eur", "€100 to $".
/// </summary>
public static class CurrencyParser
{
    private static readonly Dictionary<string, string> SymbolToCode = new()
    {
        ["$"] = "USD",
        ["€"] = "EUR",
        ["£"] = "GBP",
        ["¥"] = "JPY",
        ["₹"] = "INR",
    };

    private static readonly Dictionary<string, string> AliasToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        // ISO codes
        ["usd"] = "USD", ["eur"] = "EUR", ["gbp"] = "GBP", ["jpy"] = "JPY",
        ["inr"] = "INR", ["cad"] = "CAD", ["aud"] = "AUD", ["chf"] = "CHF",
        ["cny"] = "CNY", ["hkd"] = "HKD", ["sgd"] = "SGD", ["nzd"] = "NZD",
        ["mxn"] = "MXN", ["brl"] = "BRL", ["zar"] = "ZAR", ["krw"] = "KRW",
        ["sek"] = "SEK", ["nok"] = "NOK", ["dkk"] = "DKK", ["pln"] = "PLN",
        ["try"] = "TRY", ["aed"] = "AED", ["sar"] = "SAR", ["thb"] = "THB",
        ["php"] = "PHP", ["idr"] = "IDR", ["myr"] = "MYR", ["vnd"] = "VND",
        ["twd"] = "TWD", ["ils"] = "ILS", ["egp"] = "EGP", ["ngn"] = "NGN",
        ["pkr"] = "PKR", ["bdt"] = "BDT",
        // names
        ["dollar"] = "USD", ["dollars"] = "USD", ["buck"] = "USD", ["bucks"] = "USD",
        ["euro"] = "EUR", ["euros"] = "EUR",
        ["pound"] = "GBP", ["pounds"] = "GBP", ["quid"] = "GBP",
        ["yen"] = "JPY",
        ["rupee"] = "INR", ["rupees"] = "INR",
        ["franc"] = "CHF", ["francs"] = "CHF",
        ["yuan"] = "CNY", ["renminbi"] = "CNY",
        ["won"] = "KRW", ["lira"] = "TRY",
        ["real"] = "BRL", ["reais"] = "BRL", ["rand"] = "ZAR",
        ["peso"] = "MXN", ["pesos"] = "MXN",
        ["baht"] = "THB",
    };

    private static readonly Regex AmountRe = new(
        @"(?<sym>[$€£¥₹])?\s*(?<num>\d[\d,]*\.?\d*)\s*(?<sym2>[$€£¥₹])?",
        RegexOptions.Compiled);

    public static IReadOnlySet<string> SupportedCodes =>
        new HashSet<string>(AliasToCode.Values, StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? input, out CurrencyQuery query)
    {
        query = new CurrencyQuery();
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        if (s.Length > 120) return false;

        // Find currency mentions in order.
        var mentions = FindMentions(s);
        if (mentions.Count < 2) return false;

        // Amount: first number in the string; defaults to 1.
        double amount = 1;
        var m = AmountRe.Match(s);
        if (m.Success && double.TryParse(m.Groups["num"].Value.Replace(",", ""),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0 && a < 1e15)
            amount = a;

        // If a symbol is attached to the amount, it wins for the "from" side.
        string? amountSym = null;
        if (m.Success)
        {
            var g1 = m.Groups["sym"].Value;
            var g2 = m.Groups["sym2"].Value;
            var sym = !string.IsNullOrEmpty(g1) ? g1 : g2;
            if (!string.IsNullOrEmpty(sym) && SymbolToCode.TryGetValue(sym, out var c))
                amountSym = c;
        }

        string from, to;
        if (amountSym is not null)
        {
            // "$100 to €": from = symbol side, to = the *other* mention.
            from = amountSym;
            to = mentions.First(x => !string.Equals(x, from, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            from = mentions[0];
            to = mentions[1];
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;

        query = new CurrencyQuery { Amount = amount, From = from.ToUpperInvariant(), To = to.ToUpperInvariant() };
        return true;
    }

    private static List<string> FindMentions(string s)
    {
        var found = new List<(int idx, string code)>();
        var lower = s.ToLowerInvariant();

        foreach (var kv in AliasToCode)
        {
            // word-boundary match for multi-char aliases; codes/names only.
            var pat = $@"(?<![a-z]){Regex.Escape(kv.Key)}(?![a-z])";
            foreach (Match mm in Regex.Matches(lower, pat))
                found.Add((mm.Index, kv.Value));
        }
        // Symbols (single char, no boundaries)
        foreach (var kv in SymbolToCode)
        {
            int i = s.IndexOf(kv.Key, StringComparison.Ordinal);
            while (i >= 0) { found.Add((i, kv.Value)); i = s.IndexOf(kv.Key, i + 1, StringComparison.Ordinal); }
        }

        return found.OrderBy(x => x.idx).Select(x => x.code).ToList();
    }
}
