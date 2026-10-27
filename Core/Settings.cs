using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace GlassIsland.Core;

public sealed class TodoDto
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = [];
    public List<TodoDto> Todos { get; set; } = [];

    /// <summary>"Manual" or "Auto" (light between LightHour and DarkHour).</summary>
    public string ThemeMode { get; set; } = "Manual";
    public int LightHour { get; set; } = 7;
    public int DarkHour { get; set; } = 19;

    public bool MatchAccentToWallpaper { get; set; }
    /// <summary>When a player only provides tiny artwork (Firefox: 60 px), look up 600 px art on iTunes.</summary>
    public bool OnlineArtwork { get; set; } = true;
    /// <summary>Gemini model for the assistant (the API key itself is stored separately, encrypted).</summary>
    public string AssistantModel { get; set; } = "gemini-3.8-flash";
    public bool LiquidGlass { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Seconds of no interaction before the pill tucks into a line; 0 = never.</summary>
    public int TuckSeconds { get; set; } = 4;
    /// <summary>"Line" (slim indicator) or "Hidden" (nothing; hover the top edge to reveal).</summary>
    public string TuckStyle { get; set; } = "Line";
    public int FocusMinutes { get; set; } = 25;
    public int RotateMinutes { get; set; }
    /// <summary>-1 = all monitors, otherwise the Wallpaper Engine monitor index.</summary>
    public int Monitor { get; set; } = -1;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassIsland");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static DispatcherTimer? _saveTimer;

    public static string DataDir => Dir;

    /// <summary>Set during design snapshots so demo data never touches the user's settings.</summary>
    public static bool ReadOnly { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    /// <summary>Debounced save, so rapid toggles cost a single disk write.</summary>
    public void Save()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are best effort */ }
    }
}
