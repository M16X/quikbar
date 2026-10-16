using System.Text.Json;
using System.Text.RegularExpressions;
using quikbar.Models;

namespace quikbar.Services;

/// <summary>
/// Dictionary lookup: prefix parsing + dictionaryapi.dev JSON parsing + cache.
/// Explicit prefix only ("define|def|meaning|dict ...") so single-word app
/// searches are never hijacked. No API key.
/// </summary>
public sealed class DictionaryService
{
    private static readonly Regex PrefixRe = new(
        @"^\s*(?:define|def|meaning(?:\s+of)?|dict(?:ionary)?|lookup)\s+(?<w>[a-zA-Z][a-zA-Z\-']{1,39})\s*[.?!]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar", "dict-cache.json");

    public static bool TryParseQuery(string? input, out string word)
    {
        word = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = PrefixRe.Match(input);
        if (!m.Success) return false;
        word = m.Groups["w"].Value.ToLowerInvariant();
        return word.Length >= 2;
    }

    public static List<DictionaryEntry> ParseJson(string json)
    {
        var out_ = new List<DictionaryEntry>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return out_;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var entry = new DictionaryEntry
            {
                Word = Str(e, "word"),
                Phonetic = Str(e, "phonetic"),
            };
            if (string.IsNullOrEmpty(entry.Phonetic) && e.TryGetProperty("phonetics", out var phs))
            {
                foreach (var p in phs.EnumerateArray())
                {
                    var t = Str(p, "text");
                    if (!string.IsNullOrEmpty(t)) { entry.Phonetic = t; break; }
                }
            }
            if (e.TryGetProperty("meanings", out var meanings))
            {
                foreach (var mm in meanings.EnumerateArray())
                {
                    var meaning = new DictionaryMeaning { PartOfSpeech = Str(mm, "partOfSpeech") };
                    if (mm.TryGetProperty("definitions", out var defs))
                    {
                        foreach (var d in defs.EnumerateArray().Take(3))
                        {
                            meaning.Definitions.Add(new DictionaryDefinition
                            {
                                Definition = Str(d, "definition"),
                                Example = Str(d, "example"),
                            });
                        }
                    }
                    if (meaning.Definitions.Count > 0) entry.Meanings.Add(meaning);
                    if (entry.Meanings.Count >= 3) break;
                }
            }
            if (!string.IsNullOrEmpty(entry.Word) && entry.Meanings.Count > 0)
                out_.Add(entry);
            if (out_.Count >= 2) break;
        }
        return out_;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- cache (word -> entries + fetchedAt), path-injectable ----

    public static Dictionary<string, (List<DictionaryEntry> Entries, DateTime FetchedAt)> LoadCache(string? path = null)
    {
        try
        {
            path ??= CachePath;
            if (!File.Exists(path)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, CachedWord>>(File.ReadAllText(path));
            var d = new Dictionary<string, (List<DictionaryEntry>, DateTime)>(StringComparer.OrdinalIgnoreCase);
            if (raw is null) return d;
            foreach (var kv in raw) d[kv.Key] = (kv.Value.Entries ?? new(), kv.Value.FetchedAt);
            return d;
        }
        catch { return new(); }
    }

    public static void SaveToCache(string word, List<DictionaryEntry> entries, string? path = null)
    {
        try
        {
            path ??= CachePath;
            var cache = new Dictionary<string, CachedWord>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                try
                {
                    cache = JsonSerializer.Deserialize<Dictionary<string, CachedWord>>(File.ReadAllText(path))
                        ?? new Dictionary<string, CachedWord>(StringComparer.OrdinalIgnoreCase);
                }
                catch { }
            }
            cache[word.ToLowerInvariant()] = new CachedWord { Entries = entries, FetchedAt = DateTime.UtcNow };
            // cap 200 words
            while (cache.Count > 200)
            {
                var oldest = cache.OrderBy(kv => kv.Value.FetchedAt).First().Key;
                cache.Remove(oldest);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public sealed class CachedWord
    {
        public List<DictionaryEntry> Entries { get; set; } = new();
        public DateTime FetchedAt { get; set; }
    }

    public async Task<List<DictionaryEntry>?> FetchAsync(string word, CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.GetAsync(
                "https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word), ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            var entries = ParseJson(json);
            if (entries.Count > 0) SaveToCache(word, entries);
            return entries;
        }
        catch { return null; }
    }
}
