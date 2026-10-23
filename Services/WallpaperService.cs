using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassIsland.Core;
using Microsoft.Win32;

namespace GlassIsland.Services;

public sealed class WallpaperItem : Observable
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }          // scene, video, web, image
    public required string Dir { get; init; }
    public required string ProjectFile { get; init; }   // project.json (WE) or the image itself
    public string? PreviewPath { get; init; }
    public string[] Tags { get; init; } = [];
    public string TypeLabel => Type switch { "video" => "Video", "web" => "Web", "scene" => "Scene", "image" => "Image", _ => Type };

    bool _fav, _current;
    public bool IsFavorite { get => _fav; set => Set(ref _fav, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }

    ImageSource? _thumb;
    bool _requested;

    /// <summary>Lazy: the first binding read queues a background decode, so only visible tiles cost memory.</summary>
    public ImageSource? Thumb
    {
        get
        {
            if (_thumb == null && !_requested && PreviewPath != null)
            {
                _requested = true;
                ThumbLoader.Request(this);
            }
            return _thumb;
        }
    }

    internal bool HasThumb => _thumb != null;

    internal void SetThumb(ImageSource? img)
    {
        _thumb = img;
        Raise(nameof(Thumb));
    }

    internal void DropThumb()
    {
        if (_thumb == null) return;
        _thumb = null;
        _requested = false;
        Raise(nameof(Thumb));
    }
}

/// <summary>Single background worker; newest requests first; LRU cap keeps thumbnail memory bounded.</summary>
public static class ThumbLoader
{
    const int Cap = 220;
    const int DecodeWidth = 176;
    static readonly LinkedList<WallpaperItem> Pending = new();
    static readonly LinkedList<WallpaperItem> Loaded = new();
    static readonly object Gate = new();
    static bool _running;

    public static void Request(WallpaperItem item)
    {
        lock (Gate)
        {
            Pending.AddFirst(item);
            while (Pending.Count > 80) Pending.RemoveLast(); // tiles scrolled past long ago
            if (_running) return;
            _running = true;
        }
        Task.Run(Work);
    }

    static void Work()
    {
        while (true)
        {
            WallpaperItem item;
            lock (Gate)
            {
                if (Pending.Count == 0) { _running = false; return; }
                item = Pending.First!.Value;
                Pending.RemoveFirst();
            }
            if (item.HasThumb || item.PreviewPath == null) continue;
            var img = LoadThumb(item.PreviewPath, DecodeWidth);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                item.SetThumb(img);
                Loaded.AddLast(item);
                while (Loaded.Count > Cap)
                {
                    Loaded.First!.Value.DropThumb();
                    Loaded.RemoveFirst();
                }
            });
        }
    }

    static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassIsland", "thumbs");

    /// <summary>
    /// Thumbnail with a disk cache: the (often multi-MB) preview is decoded once ever and saved as a ~8 KB JPEG.
    /// Reading big previews makes WPF churn large buffers, and the resulting GCs were the main scroll stutter.
    /// </summary>
    static BitmapSource? LoadThumb(string path, int width)
    {
        string? cached = null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            cached = Path.Combine(CacheDir, $"{StableHash(path.ToLowerInvariant()):x16}_{stamp:x}_{width}.jpg");
            if (File.Exists(cached)) return Decode(cached, 0);
        }
        catch { cached = null; }

        var img = Decode(path, width);
        if (img != null && cached != null)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(img));
                using var fs = File.Create(cached);
                enc.Save(fs);
            }
            catch { /* cache is optional */ }
        }
        return img;
    }

    /// <summary>FNV-1a: stable across runs (string.GetHashCode is randomized per process).</summary>
    static ulong StableHash(string s)
    {
        ulong h = 14695981039346656037;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    public static BitmapSource? Decode(string path, int width)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            if (width > 0) b.DecodePixelWidth = width;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Called when the panel has been closed for a while.</summary>
    public static void ReleaseAll()
    {
        lock (Gate) Pending.Clear();
        foreach (var i in Loaded) i.DropThumb();
        Loaded.Clear();
    }
}

public sealed class WallpaperService
{
    public string? WeExe { get; }
    public string? WeRoot { get; }
    public bool HasWallpaperEngine => WeExe != null;
    public string SourceName => HasWallpaperEngine ? "Wallpaper Engine" : "Windows";

    static readonly string CacheFile = Path.Combine(AppSettings.DataDir, "wallpaper-index.json");

    public WallpaperService()
    {
        try
        {
            var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\WallpaperEngine", "installPath", null) as string;
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                WeRoot = Path.GetDirectoryName(p);
                // Prefer the 64-bit host; it accepts the same -control commands.
                var x64 = Path.Combine(WeRoot!, "wallpaper64.exe");
                WeExe = File.Exists(x64) ? x64 : p;
            }
        }
        catch { /* no Wallpaper Engine */ }
    }

    IEnumerable<string> Roots()
    {
        if (WeRoot == null) yield break;
        // ...\steamapps\common\wallpaper_engine -> ...\steamapps\workshop\content\431960
        var steamapps = Path.GetFullPath(Path.Combine(WeRoot, "..", ".."));
        yield return Path.Combine(steamapps, "workshop", "content", "431960");
        yield return Path.Combine(WeRoot, "projects", "myprojects");
        yield return Path.Combine(WeRoot, "projects", "defaultprojects");
    }

    sealed class CacheEntry
    {
        public string Dir { get; set; } = "";
        public long Stamp { get; set; }
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Preview { get; set; }
        public string[] Tags { get; set; } = [];
    }

    public Task<List<WallpaperItem>> LoadAsync() => Task.Run(() => HasWallpaperEngine ? LoadEngine() : LoadWindows());

    List<WallpaperItem> LoadEngine()
    {
        Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CacheFile))
                foreach (var e in JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(CacheFile)) ?? [])
                    cache[e.Dir] = e;
        }
        catch { /* rebuild */ }

        var fresh = new List<CacheEntry>();
        var items = new List<WallpaperItem>();
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var project = Path.Combine(dir, "project.json");
                if (!File.Exists(project)) continue;
                long stamp = File.GetLastWriteTimeUtc(project).Ticks;
                if (!cache.TryGetValue(dir, out var e) || e.Stamp != stamp)
                    e = Parse(dir, project, stamp);
                if (e == null) continue;
                fresh.Add(e);
                if (e.Type is "preset") continue;
                items.Add(new WallpaperItem
                {
                    Id = Path.GetFileName(dir),
                    Title = e.Title,
                    Type = e.Type,
                    Dir = dir,
                    ProjectFile = project,
                    PreviewPath = e.Preview != null && File.Exists(Path.Combine(dir, e.Preview)) ? Path.Combine(dir, e.Preview) : null,
                    Tags = e.Tags,
                });
            }
        }

        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(fresh));
        }
        catch { /* cache is optional */ }

        return [.. items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    static CacheEntry? Parse(string dir, string project, long stamp)
    {
        try
        {
            var bytes = File.ReadAllBytes(project);
            var span = bytes.AsMemory();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) span = span[3..];
            using var doc = JsonDocument.Parse(span, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var tags = r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            var title = Str("title");
            return new CacheEntry
            {
                Dir = dir,
                Stamp = stamp,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(dir) : title.Trim(),
                Type = Str("type").ToLowerInvariant(),
                Preview = Str("preview") is { Length: > 0 } p ? p : null,
                Tags = tags,
            };
        }
        catch { return null; }
    }

    static List<WallpaperItem> LoadWindows()
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper"),
        };
        var items = new List<WallpaperItem>();
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories).Where(f => exts.Contains(Path.GetExtension(f))).Take(400))
                items.Add(new WallpaperItem
                {
                    Id = f, Title = Path.GetFileNameWithoutExtension(f), Type = "image",
                    Dir = Path.GetDirectoryName(f)!, ProjectFile = f, PreviewPath = f,
                });
        return items;
    }

    /// <summary>Folders of the wallpapers Wallpaper Engine currently shows, and its recent history.</summary>
    public (HashSet<string> Current, List<string> Recent) ReadState()
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recent = new List<string>();
        if (WeRoot == null) return (current, recent);
        try
        {
            var cfg = Path.Combine(WeRoot, "config.json");
            using var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var user in doc.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object || !user.Value.TryGetProperty("general", out var general)) continue;
                if (general.TryGetProperty("wallpaperconfig", out var wc)) Collect(wc, current);
                if (general.TryGetProperty("wallpaperconfigrecent", out var rc) && rc.ValueKind == JsonValueKind.Array)
                    foreach (var entry in rc.EnumerateArray())
                    {
                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (entry.TryGetProperty("config", out var c)) Collect(c, set); else Collect(entry, set);
                        foreach (var s in set) if (!recent.Contains(s, StringComparer.OrdinalIgnoreCase)) recent.Add(s);
                    }
            }
        }
        catch { /* WE may be writing the file; try again later */ }
        return (current, recent);
    }

    static void Collect(JsonElement wallpaperConfig, HashSet<string> into)
    {
        if (wallpaperConfig.ValueKind != JsonValueKind.Object || !wallpaperConfig.TryGetProperty("selectedwallpapers", out var sel)) return;
        foreach (var mon in sel.EnumerateObject())
            if (mon.Value.TryGetProperty("file", out var f) && f.GetString() is { Length: > 0 } file)
            {
                var dir = Path.GetDirectoryName(file.Replace('/', '\\'));
                if (dir != null) into.Add(dir);
            }
    }

    public void Apply(WallpaperItem item, int monitor)
    {
        if (item.Type == "image")
        {
            Native.SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, item.ProjectFile, 0x01 | 0x02);
            return;
        }
        var args = $"-control openWallpaper -file \"{item.ProjectFile}\"";
        if (monitor >= 0) args += $" -monitor {monitor}";
        Run(args);
    }

    /// <summary>pause | play | mute | unmute | stop</summary>
    public void Control(string command) => Run($"-control {command}");

    void Run(string args)
    {
        if (WeExe == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(WeExe, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { App.Log("Wallpaper Engine command failed: " + ex.Message); }
    }
}
