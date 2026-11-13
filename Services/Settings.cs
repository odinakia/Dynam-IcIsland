using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Services;

public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(App.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Пустое значение — город определяется по IP.</summary>
    public string City { get; set; } = "";

    public bool HideInFullscreen { get; set; } = true;

    public bool PeekOnTrackChange { get; set; } = true;

    public bool ShowWeather { get; set; }

    public bool ShowClock { get; set; }

    public bool ShowBattery { get; set; }

    public List<string> Shelf { get; set; } = new();

    [JsonIgnore]
    public bool IsDirty { get; private set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            App.Log("settings load: " + ex.Message);
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            IsDirty = false;
        }
        catch (Exception ex)
        {
            App.Log("settings save: " + ex.Message);
        }
    }

    public void SaveShelf(IEnumerable<string> paths)
    {
        Shelf = paths.Take(24).ToList();
        Save();
    }
}
