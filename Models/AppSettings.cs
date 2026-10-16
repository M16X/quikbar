namespace quikbar.Models;

public sealed class AppSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new() { "gpt-4o-mini", "gpt-4o", "gpt-3.5-turbo" };
    public string DefaultModel { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.7;
    public string SystemPrompt { get; set; } = "You are a helpful assistant inside a launcher. Keep answers concise.";
    public string Hotkey { get; set; } = "Alt+Space";
    public bool LaunchAtStartup { get; set; } = false;
    public bool HideOnFocusLost { get; set; } = true;
}
