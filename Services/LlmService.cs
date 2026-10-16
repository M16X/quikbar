using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace quikbar.Services;

public sealed class LlmService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static string NormalizeEndpoint(string endpoint)
    {
        endpoint = (endpoint ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(endpoint)) return "https://api.openai.com/v1";
        return endpoint;
    }

    public async Task<List<string>> ListModelsAsync(string endpoint, string apiKey, CancellationToken ct = default)
    {
        endpoint = NormalizeEndpoint(endpoint);
        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id))
                {
                    var v = id.GetString();
                    if (!string.IsNullOrEmpty(v)) list.Add(v);
                }
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public async Task<string> AskAsync(string endpoint, string apiKey, string model, double temperature,
        string systemPrompt, IReadOnlyList<(string Role, string Content)> history, string question,
        CancellationToken ct = default)
    {
        endpoint = NormalizeEndpoint(endpoint);
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        foreach (var h in history)
            messages.Add(new { role = h.Role, content = h.Content });
        messages.Add(new { role = "user", content = question });

        var payload = new { model, messages, temperature };
        var body = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM error {(int)res.StatusCode}: {Trim(text, 500)}");
        using var doc = JsonDocument.Parse(text);
        var answer = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return (answer ?? string.Empty).Trim();
    }

    public async Task<string> AskStreamingAsync(string endpoint, string apiKey, string model, double temperature,
        string systemPrompt, IReadOnlyList<(string Role, string Content)> history, string question,
        Action<string> onDelta, CancellationToken ct = default)
    {
        endpoint = NormalizeEndpoint(endpoint);
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        foreach (var h in history)
            messages.Add(new { role = h.Role, content = h.Content });
        messages.Add(new { role = "user", content = question });

        var payload = new { model, messages, temperature, stream = true };
        var body = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var errText = string.Empty;
        if (!res.IsSuccessStatusCode)
        {
            errText = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"LLM error {(int)res.StatusCode}: {Trim(errText, 500)}");
        }

        var sb = new StringBuilder();
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim();
            if (line == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var ch = choices[0];
                string? delta = null;
                if (ch.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c))
                    delta = c.GetString();
                else if (ch.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var mc))
                    delta = mc.GetString();
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    onDelta(sb.ToString());
                }
            }
            catch { /* skip partial */ }
        }
        return sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
