using System.Globalization;
using System.Text.Json;
using quikbar.Models;

namespace quikbar.Services;

/// <summary>
/// Currency rates: conversion math (pure/testable) + file cache + HTTP fetch.
/// Free no-key sources: Frankfurter (ECB) primary, open.er-api.com fallback.
/// </summary>
public sealed class CurrencyService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar", "currency-cache.json");

    // ---- pure math ----

    public static bool IsStale(DateTime fetchedAt, DateTime now) =>
        fetchedAt == DateTime.MinValue || (now - fetchedAt) > CacheTtl;

    /// <summary>Convert via base cross-rate. Rates are units-per-Base (Frankfurter shape).</summary>
    public static bool TryConvert(double amount, string from, string to,
        CurrencyCache? cache, out double result)
    {
        result = 0;
        if (cache?.Rates is null || cache.Rates.Count == 0) return false;
        if (amount <= 0 || amount >= 1e15) return false;
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();
        if (from == to) { result = amount; return true; }

        if (!TryRate(from, cache, out var rf) || !TryRate(to, cache, out var rt))
            return false;
        // amount in Base = amount / rf; result = inBase * rt
        result = amount / rf * rt;
        return double.IsFinite(result) && result > 0;
    }

    private static bool TryRate(string code, CurrencyCache cache, out double r)
    {
        if (string.Equals(code, cache.Base, StringComparison.OrdinalIgnoreCase))
        {
            r = 1;
            return true;
        }
        return cache.Rates.TryGetValue(code, out r) && r > 0 && double.IsFinite(r);
    }

    public static string Format(double v, string code)
    {
        int dec = code.Equals("JPY", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
        return v.ToString($"#,##0.{new string('0', dec)}", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') is var t && dec == 0
            ? v.ToString("#,##0", CultureInfo.InvariantCulture)
            : v.ToString($"#,##0.{new string('#', dec)}", CultureInfo.InvariantCulture);
    }

    public static double UnitRate(string from, string to, CurrencyCache cache)
    {
        TryConvert(1, from, to, cache, out var r);
        return r;
    }

    // ---- cache I/O (path-injectable for tests) ----

    public static CurrencyCache? LoadCache(string? path = null)
    {
        try
        {
            path ??= CachePath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CurrencyCache>(json);
        }
        catch { return null; }
    }

    public static void SaveCache(CurrencyCache cache, string? path = null)
    {
        try
        {
            path ??= CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    // ---- network ----

    public async Task<CurrencyCache?> RefreshAsync(CancellationToken ct = default)
    {
        var c = await TryFetch($"https://api.frankfurter.app/v1/latest?base=USD", "USD", ct)
            ?? await TryFetch($"https://open.er-api.com/v6/latest/USD", "USD", ct, erApi: true);
        if (c is not null) SaveCache(c);
        return c;
    }

    private static async Task<CurrencyCache?> TryFetch(string url, string b, CancellationToken ct, bool erApi = false)
    {
        try
        {
            using var res = await Http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement ratesEl;
            string date = "";
            if (erApi)
            {
                if (!root.TryGetProperty("rates", out ratesEl)) return null;
                if (root.TryGetProperty("time_last_update_utc", out var t)) date = t.GetString() ?? "";
            }
            else
            {
                if (!root.TryGetProperty("rates", out ratesEl)) return null;
                if (root.TryGetProperty("date", out var d)) date = d.GetString() ?? "";
            }
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ratesEl.EnumerateObject())
                if (p.Value.TryGetDouble(out var v) && v > 0) rates[p.Name] = v;
            if (rates.Count == 0) return null;
            return new CurrencyCache { Base = b, Rates = rates, Date = date, FetchedAt = DateTime.UtcNow };
        }
        catch { return null; }
    }
}
