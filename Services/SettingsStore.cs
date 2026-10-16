using System.Text.Json;
using quikbar.Models;

namespace quikbar.Services;

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar");
    private static readonly string Path_ = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s is not null)
                    Current = s;
            }
        }
        catch { /* keep defaults */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(Current, JsonOpts));
            Changed?.Invoke();
        }
        catch { }
    }

    public static void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
    }
}
