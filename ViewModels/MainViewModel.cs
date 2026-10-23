using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GlassIsland.Core;
using GlassIsland.Services;

namespace GlassIsland.ViewModels;

public sealed record DayItem(string Letter, string Number, bool IsToday);

public sealed class MainViewModel : Observable
{
    public AppSettings Settings { get; }
    public MediaService Media { get; } = new();
    public WallpapersViewModel Walls { get; }
    public ThemeViewModel Theme { get; }
    public FocusViewModel Focus { get; }

    /// <summary>(title, subtitle, image, glyph) – shown as a short live activity in the island.</summary>
    public event Action<string, string, ImageSource?, string>? Toast;
    public void ShowToast(string title, string subtitle, ImageSource? image = null, string glyph = "") => Toast?.Invoke(title, subtitle, image, glyph);

    readonly DispatcherTimer _clock;
    string _time = "", _date = "", _month = "";
    IReadOnlyList<DayItem> _week = [];
    double _volume;
    bool _muted;

    string _hotkey = "Ctrl + Alt + Space";
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }

    public string Time { get => _time; private set => Set(ref _time, value); }
    public string Date { get => _date; private set => Set(ref _date, value); }
    public string Month { get => _month; private set => Set(ref _month, value); }
    public IReadOnlyList<DayItem> Week { get => _week; private set => Set(ref _week, value); }

    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, Math.Round(value))) { AudioService.SetVolume(_volume); if (_muted && _volume > 0) IsMuted = false; } }
    }

    public bool IsMuted
    {
        get => _muted;
        set { if (Set(ref _muted, value)) AudioService.SetMute(value); }
    }

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        Media.OnlineArtwork = () => Settings.OnlineArtwork;
        Theme = new ThemeViewModel(this);
        Walls = new WallpapersViewModel(this);
        Focus = new FocusViewModel(this);

        _clock = new DispatcherTimer(DispatcherPriority.Background);
        _clock.Tick += (_, _) => Tick();
        Tick();
    }

    void Tick()
    {
        var now = DateTime.Now;
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH:mm" : "h:mm";
        Time = now.ToString(pattern, CultureInfo.CurrentCulture);
        Date = now.ToString("ddd, d MMM", CultureInfo.CurrentCulture);
        if (Month != now.ToString("MMM", CultureInfo.CurrentCulture) || Week.Count == 0 || !Week[3].Number.Equals(now.Day.ToString()))
        {
            Month = now.ToString("MMM", CultureInfo.CurrentCulture);
            Week = Enumerable.Range(-3, 7).Select(d =>
            {
                var day = now.Date.AddDays(d);
                return new DayItem(day.ToString("ddd", CultureInfo.CurrentCulture)[..1].ToUpper(), day.Day.ToString("00"), d == 0);
            }).ToList();
        }
        Theme.CheckSchedule();
        // Wake exactly on the next minute boundary: one timer tick per minute.
        _clock.Interval = TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 30);
        _clock.Start();
    }

    public void RefreshAudio()
    {
        var (v, m) = AudioService.Get();
        _volume = v; _muted = m;
        Raise(nameof(Volume)); Raise(nameof(IsMuted));
    }
}

/// <summary>
/// One grid row with fixed slots. The row template binds 5 tiles to Item0..Item4, so when the virtualizing
/// list recycles a row it only re-binds those tiles instead of regenerating them.
/// </summary>
public sealed class WallpaperRow(WallpaperItem[] items)
{
    public WallpaperItem[] Items { get; } = items;
    public WallpaperItem? Item0 => Items.Length > 0 ? Items[0] : null;
    public WallpaperItem? Item1 => Items.Length > 1 ? Items[1] : null;
    public WallpaperItem? Item2 => Items.Length > 2 ? Items[2] : null;
    public WallpaperItem? Item3 => Items.Length > 3 ? Items[3] : null;
    public WallpaperItem? Item4 => Items.Length > 4 ? Items[4] : null;
}

public sealed class WallpapersViewModel : Observable
{
    public const int Columns = 5;
    readonly MainViewModel _root;
    readonly WallpaperService _svc = new();
    readonly Random _rng = new();
    readonly DispatcherTimer _rotate = new();
    List<WallpaperItem> _all = [];
    List<string> _recent = [];
    IReadOnlyList<WallpaperRow> _rows = [];
    string _filter = "All", _search = "", _status = "";
    bool _loading, _loaded, _paused, _muted;
    WallpaperItem? _current;
    int _monitor;

    public WallpaperService Service => _svc;
    public IReadOnlyList<WallpaperRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public WallpaperItem? Current { get => _current; private set { if (Set(ref _current, value)) Raise(nameof(CurrentTitle)); } }
    public string CurrentTitle => _current?.Title ?? "—";
    public bool IsEngine => _svc.HasWallpaperEngine;
    public bool IsPaused { get => _paused; private set => Set(ref _paused, value); }
    public bool IsMuted { get => _muted; private set => Set(ref _muted, value); }
    public bool HasMultipleMonitors { get; } = Native.MonitorCount() > 1;
    public IReadOnlyList<string> MonitorOptions { get; }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) Rebuild(); }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Rebuild(); }
    }

    /// <summary>0 = all monitors, 1..n = that monitor.</summary>
    public int Monitor
    {
        get => _monitor;
        set { if (Set(ref _monitor, value)) { _root.Settings.Monitor = value - 1; _root.Settings.Save(); } }
    }

    public Relay ApplyCommand { get; }
    public Relay FavoriteCommand { get; }
    public Relay ShuffleCommand { get; }
    public Relay PauseCommand { get; }
    public Relay MuteCommand { get; }
    public Relay FilterCommand { get; }
    public Relay MonitorCommand { get; }

    public WallpapersViewModel(MainViewModel root)
    {
        _root = root;
        _monitor = Math.Max(0, root.Settings.Monitor + 1);
        MonitorOptions = ["All", .. Enumerable.Range(1, Native.MonitorCount()).Select(i => i.ToString())];

        ApplyCommand = new Relay(p => { if (p is WallpaperItem w) Apply(w); });
        FavoriteCommand = new Relay(p => { if (p is WallpaperItem w) ToggleFavorite(w); });
        ShuffleCommand = new Relay(Shuffle);
        PauseCommand = new Relay(() =>
        {
            IsPaused = !IsPaused;
            _svc.Control(IsPaused ? "pause" : "play");
        });
        MuteCommand = new Relay(() =>
        {
            IsMuted = !IsMuted;
            _svc.Control(IsMuted ? "mute" : "unmute");
        });
        FilterCommand = new Relay(p => Filter = p as string ?? "All");
        MonitorCommand = new Relay(p => { if (int.TryParse(p as string, out var i)) Monitor = i; else Monitor = 0; });

        _rotate.Tick += (_, _) => Shuffle();
        UpdateRotation();
    }

    Task? _loadTask;

    /// <summary>Indexes wallpapers once; concurrent callers share the same in-flight task.</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : _loadTask ??= LoadAsync();

    async Task LoadAsync()
    {
        IsLoading = true;
        Status = "Indexing wallpapers…";
        try
        {
            _all = await _svc.LoadAsync();
            var favs = new HashSet<string>(_root.Settings.Favorites);
            foreach (var w in _all) w.IsFavorite = favs.Contains(w.Id);
            RefreshState();
            _loaded = true;
        }
        catch (Exception ex)
        {
            App.Log("Wallpaper index failed: " + ex);
            _loadTask = null; // allow a retry next time the tab opens
        }
        finally
        {
            IsLoading = false;
            Rebuild();
        }
    }

    public void RefreshState()
    {
        if (!_svc.HasWallpaperEngine) return;
        var (current, recent) = _svc.ReadState();
        _recent = recent;
        WallpaperItem? cur = null;
        foreach (var w in _all)
        {
            w.IsCurrent = current.Contains(w.Dir);
            if (w.IsCurrent && cur == null) cur = w;
        }
        Current = cur;
    }

    void Rebuild()
    {
        IEnumerable<WallpaperItem> q = _all;
        if (_filter == "Favorites") q = q.Where(w => w.IsFavorite);
        else if (_filter == "Recent")
        {
            var order = _recent.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i, StringComparer.OrdinalIgnoreCase);
            q = q.Where(w => order.ContainsKey(w.Dir)).OrderBy(w => order[w.Dir]);
        }
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var s = _search.Trim();
            q = q.Where(w => w.Title.Contains(s, StringComparison.CurrentCultureIgnoreCase) ||
                             w.Tags.Any(t => t.Contains(s, StringComparison.CurrentCultureIgnoreCase)) ||
                             w.TypeLabel.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        }
        var list = q.ToList();
        Rows = list.Chunk(Columns).Select(c => new WallpaperRow(c)).ToList();
        Status = IsLoading ? "Indexing wallpapers…"
            : _all.Count == 0 ? (_svc.HasWallpaperEngine ? "No wallpapers found" : "No wallpapers found in Pictures\\Wallpapers")
            : list.Count == 0 ? (_filter == "Favorites" ? "Star a wallpaper to pin it here" : "Nothing matches")
            : $"{list.Count} of {_all.Count}";
    }

    public void Apply(WallpaperItem w)
    {
        _svc.Apply(w, _root.Settings.Monitor);
        foreach (var x in _all) if (_root.Settings.Monitor < 0 || ReferenceEquals(x, Current)) x.IsCurrent = false;
        w.IsCurrent = true;
        Current = w;
        IsPaused = false;
        if (!_recent.Contains(w.Dir)) _recent.Insert(0, w.Dir);

        var preview = w.PreviewPath != null ? ThumbLoader.Decode(w.PreviewPath, 96) : null;
        if (_root.Settings.MatchAccentToWallpaper && ColorTools.Dominant(preview) is { } c)
            _root.Theme.ApplyAccent(c);
        _root.ShowToast(w.Title, _svc.HasWallpaperEngine ? $"{w.TypeLabel} wallpaper applied" : "Wallpaper applied", preview, "");
    }

    void ToggleFavorite(WallpaperItem w)
    {
        w.IsFavorite = !w.IsFavorite;
        var favs = _root.Settings.Favorites;
        if (w.IsFavorite) { if (!favs.Contains(w.Id)) favs.Add(w.Id); }
        else favs.Remove(w.Id);
        _root.Settings.Save();
        if (_filter == "Favorites") Rebuild();
    }

    async void Shuffle()
    {
        await EnsureLoadedAsync();
        var pool = _rows.SelectMany(r => r.Items).Where(w => !w.IsCurrent).ToList();
        if (pool.Count == 0) pool = _all.Where(w => !w.IsCurrent).ToList();
        if (pool.Count > 0) Apply(pool[_rng.Next(pool.Count)]);
    }

    public void UpdateRotation()
    {
        _rotate.Stop();
        if (_root.Settings.RotateMinutes > 0)
        {
            _rotate.Interval = TimeSpan.FromMinutes(_root.Settings.RotateMinutes);
            _rotate.Start();
        }
    }
}

public sealed class Swatch(Color color) : Observable
{
    bool _selected;
    public Color Color { get; } = color;
    public SolidColorBrush Brush { get; } = Freeze(new SolidColorBrush(color));
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

public sealed class ThemeViewModel : Observable
{
    readonly MainViewModel _root;
    readonly ThemeService _svc = new();
    bool _dark, _transparency, _accentBar, _startup;
    Color _accent;

    public IReadOnlyList<Swatch> Swatches { get; } =
    [
        new(Color.FromRgb(0x4F, 0x8C, 0xFF)), new(Color.FromRgb(0x7C, 0x5C, 0xFF)), new(Color.FromRgb(0xE8, 0x5D, 0x9A)),
        new(Color.FromRgb(0xFF, 0x5F, 0x57)), new(Color.FromRgb(0xFF, 0x9F, 0x0A)), new(Color.FromRgb(0xF2, 0xC9, 0x4C)),
        new(Color.FromRgb(0x34, 0xC7, 0x59)), new(Color.FromRgb(0x2E, 0xC4, 0xC9)), new(Color.FromRgb(0x8E, 0x9A, 0xAF)),
    ];

    public Relay ModeCommand { get; }
    public Relay AccentCommand { get; }
    public Relay RotateCommand { get; }
    public Relay TuckCommand { get; }
    public Relay TuckStyleCommand { get; }

    public event Action? GlassModeChanged;
    /// <summary>Tuck delay, tuck style or fullscreen behaviour changed.</summary>
    public event Action? IslandBehaviorChanged;

    public ThemeViewModel(MainViewModel root)
    {
        _root = root;
        _dark = _svc.IsDark;
        _transparency = _svc.Transparency;
        _accentBar = _svc.AccentOnTaskbar;
        _startup = _svc.StartWithWindows;
        SetAccentResource(_svc.GetAccent());

        ModeCommand = new Relay(p =>
        {
            var mode = p as string ?? "Dark";
            _root.Settings.ThemeMode = mode == "Auto" ? "Auto" : "Manual";
            _root.Settings.Save();
            if (mode == "Auto") CheckSchedule(force: true); else IsDark = mode == "Dark";
            Raise(nameof(Mode));
        });
        AccentCommand = new Relay(p => { if (p is Swatch s) { MatchWallpaper = false; ApplyAccent(s.Color); } });
        RotateCommand = new Relay(() =>
        {
            int[] steps = [0, 15, 30, 60, 180];
            int i = Array.IndexOf(steps, _root.Settings.RotateMinutes);
            _root.Settings.RotateMinutes = steps[(i + 1) % steps.Length];
            _root.Settings.Save();
            _root.Walls.UpdateRotation();
            Raise(nameof(RotateLabel));
            Raise(nameof(IsRotating));
        });
        TuckCommand = new Relay(() =>
        {
            int[] steps = [4, 8, 15, 0];
            int i = Array.IndexOf(steps, _root.Settings.TuckSeconds);
            _root.Settings.TuckSeconds = steps[(i + 1) % steps.Length];
            _root.Settings.Save();
            Raise(nameof(TuckLabel));
            IslandBehaviorChanged?.Invoke();
        });
        TuckStyleCommand = new Relay(() =>
        {
            _root.Settings.TuckStyle = _root.Settings.TuckStyle == "Line" ? "Hidden" : "Line";
            _root.Settings.Save();
            Raise(nameof(TuckStyleLabel));
            IslandBehaviorChanged?.Invoke();
        });
    }

    public bool OnlineArtwork
    {
        get => _root.Settings.OnlineArtwork;
        set
        {
            if (_root.Settings.OnlineArtwork == value) return;
            _root.Settings.OnlineArtwork = value;
            _root.Settings.Save();
            Raise();
        }
    }

    public string TuckLabel => _root.Settings.TuckSeconds == 0 ? "Never" : $"{_root.Settings.TuckSeconds} s";
    public string TuckStyleLabel => _root.Settings.TuckStyle == "Hidden" ? "Nothing" : "A line";

    public bool HideInFullscreen
    {
        get => _root.Settings.HideInFullscreen;
        set
        {
            if (_root.Settings.HideInFullscreen == value) return;
            _root.Settings.HideInFullscreen = value;
            _root.Settings.Save();
            Raise();
            IslandBehaviorChanged?.Invoke();
        }
    }

    public string Mode => _root.Settings.ThemeMode == "Auto" ? "Auto" : (_dark ? "Dark" : "Light");
    public string AutoHint => $"Light {_root.Settings.LightHour:00}:00 · Dark {_root.Settings.DarkHour:00}:00";

    public bool IsDark
    {
        get => _dark;
        set
        {
            if (!Set(ref _dark, value)) return;
            _svc.SetDark(value);
            Raise(nameof(Mode));
            _root.ShowToast(value ? "Dark mode" : "Light mode", "Windows theme switched", null, value ? "" : "");
        }
    }

    public Color Accent => _accent;

    public void ApplyAccent(Color c)
    {
        _svc.SetAccent(c);
        SetAccentResource(c);
    }

    void SetAccentResource(Color c)
    {
        _accent = c;
        var res = Application.Current.Resources;
        var b = new SolidColorBrush(c); b.Freeze();
        var soft = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)); soft.Freeze();
        res["AccentBrush"] = b;
        res["AccentSoftBrush"] = soft;
        res["AccentColor"] = c;
        foreach (var s in Swatches) s.IsSelected = ColorDistance(s.Color, c) < 24;
        Raise(nameof(Accent));
    }

    static double ColorDistance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    public bool MatchWallpaper
    {
        get => _root.Settings.MatchAccentToWallpaper;
        set
        {
            if (_root.Settings.MatchAccentToWallpaper == value) return;
            _root.Settings.MatchAccentToWallpaper = value;
            _root.Settings.Save();
            Raise();
            if (value && _root.Walls.Current?.PreviewPath is { } p && ColorTools.Dominant(ThumbLoader.Decode(p, 96)) is { } c)
                ApplyAccent(c);
        }
    }

    public bool Transparency { get => _transparency; set { if (Set(ref _transparency, value)) _svc.Transparency = value; } }
    public bool AccentOnTaskbar { get => _accentBar; set { if (Set(ref _accentBar, value)) _svc.AccentOnTaskbar = value; } }
    public bool StartWithWindows { get => _startup; set { if (Set(ref _startup, value)) _svc.StartWithWindows = value; } }

    public bool LiquidGlass
    {
        get => _root.Settings.LiquidGlass;
        set
        {
            if (_root.Settings.LiquidGlass == value) return;
            _root.Settings.LiquidGlass = value;
            _root.Settings.Save();
            Raise();
            GlassModeChanged?.Invoke();
        }
    }

    public bool IsRotating => _root.Settings.RotateMinutes > 0;
    public string RotateLabel => _root.Settings.RotateMinutes switch
    {
        0 => "Off", < 60 and var m => $"{m} min", var m => $"{m / 60} h"
    };

    /// <summary>Auto mode: light during the day, dark at night. Called once a minute.</summary>
    public void CheckSchedule(bool force = false)
    {
        if (_root?.Settings.ThemeMode != "Auto") return;
        int h = DateTime.Now.Hour;
        bool wantDark = h < _root.Settings.LightHour || h >= _root.Settings.DarkHour;
        if (force || wantDark != _svc.IsDark) IsDarkSilent(wantDark);
    }

    void IsDarkSilent(bool dark)
    {
        _dark = dark;
        _svc.SetDark(dark);
        Raise(nameof(IsDark));
        Raise(nameof(Mode));
    }
}

public sealed class TodoItem : Observable
{
    readonly Action _changed;
    string _text;
    bool _done;

    public TodoItem(string text, bool done, Action changed) { _text = text; _done = done; _changed = changed; }
    public string Text { get => _text; set { if (Set(ref _text, value)) _changed(); } }
    public bool Done { get => _done; set { if (Set(ref _done, value)) _changed(); } }
}

public sealed class FocusViewModel : Observable
{
    readonly MainViewModel _root;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
    DateTime _end;
    TimeSpan _remaining;
    bool _running;
    string _newTodo = "";

    public ObservableCollection<TodoItem> Todos { get; } = [];
    public IReadOnlyList<int> Presets { get; } = [15, 25, 50];

    public Relay ToggleCommand { get; }
    public Relay ResetCommand { get; }
    public Relay PresetCommand { get; }
    public Relay AddTodoCommand { get; }
    public Relay RemoveTodoCommand { get; }
    public Relay QuickStartCommand { get; }

    /// <summary>Raised when a session completes.</summary>
    public event Action? Finished;
    public event Action? RunningChanged;

    public FocusViewModel(MainViewModel root)
    {
        _root = root;
        _remaining = TimeSpan.FromMinutes(Minutes);
        foreach (var t in root.Settings.Todos) Todos.Add(new TodoItem(t.Text, t.Done, SaveTodos));
        Todos.CollectionChanged += (_, _) => SaveTodos();

        _timer.Tick += (_, _) => Tick();
        ToggleCommand = new Relay(() => { if (IsRunning) Pause(); else Start(); });
        ResetCommand = new Relay(Reset);
        PresetCommand = new Relay(p =>
        {
            if (p is int m || int.TryParse(p?.ToString(), out m))
            {
                _root.Settings.FocusMinutes = m;
                _root.Settings.Save();
                Raise(nameof(Minutes));
                Reset();
            }
        });
        AddTodoCommand = new Relay(() =>
        {
            var text = NewTodo.Trim();
            if (text.Length == 0) return;
            Todos.Insert(0, new TodoItem(text, false, SaveTodos));
            NewTodo = "";
        });
        RemoveTodoCommand = new Relay(p => { if (p is TodoItem t) Todos.Remove(t); });
        QuickStartCommand = new Relay(() => { Reset(); Start(); });
    }

    public int Minutes => _root.Settings.FocusMinutes;
    public bool IsRunning { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(ToggleGlyph)); Raise(nameof(ToggleLabel)); RunningChanged?.Invoke(); } } }
    public string ToggleGlyph => _running ? "" : "";
    public string ToggleLabel => _running ? "Pause" : (_remaining < TimeSpan.FromMinutes(Minutes) ? "Resume" : "Start");
    public bool IsActive => _running || _remaining < TimeSpan.FromMinutes(Minutes);
    public string RemainingText => $"{(int)_remaining.TotalMinutes:00}:{_remaining.Seconds:00}";
    public double Progress => 1 - _remaining.TotalSeconds / (Minutes * 60.0);
    public string NewTodo { get => _newTodo; set => Set(ref _newTodo, value ?? ""); }
    public string NextTodoText => Todos.FirstOrDefault(t => !t.Done)?.Text ?? "Nothing for today";
    public bool HasNextTodo => Todos.Any(t => !t.Done);
    public string TodoSummary { get { int open = Todos.Count(t => !t.Done); return open == 0 ? "All clear" : $"{open} open"; } }

    void SaveTodos()
    {
        _root.Settings.Todos = Todos.Select(t => new TodoDto { Text = t.Text, Done = t.Done }).ToList();
        _root.Settings.Save();
        Raise(nameof(NextTodoText)); Raise(nameof(HasNextTodo)); Raise(nameof(TodoSummary));
    }

    public void Start()
    {
        if (_remaining <= TimeSpan.Zero) _remaining = TimeSpan.FromMinutes(Minutes);
        _end = DateTime.Now + _remaining;
        IsRunning = true;
        _timer.Start();
        RaiseTime();
    }

    void Pause()
    {
        _timer.Stop();
        _remaining = _end - DateTime.Now;
        IsRunning = false;
        RaiseTime();
    }

    void Reset()
    {
        _timer.Stop();
        _remaining = TimeSpan.FromMinutes(Minutes);
        IsRunning = false;
        RaiseTime();
    }

    void Tick()
    {
        _remaining = _end - DateTime.Now;
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _timer.Stop();
            IsRunning = false;
            RaiseTime();
            System.Media.SystemSounds.Asterisk.Play();
            Finished?.Invoke();
            _remaining = TimeSpan.FromMinutes(Minutes);
            RaiseTime();
            return;
        }
        RaiseTime();
    }

    void RaiseTime()
    {
        Raise(nameof(RemainingText)); Raise(nameof(Progress)); Raise(nameof(IsActive)); Raise(nameof(ToggleLabel));
    }

    /// <summary>For design snapshots: pretend a session is mid-way.</summary>
    public void Demo(TimeSpan remaining)
    {
        _remaining = remaining;
        _end = DateTime.Now + remaining;
        IsRunning = true;
        RaiseTime();
    }
}
