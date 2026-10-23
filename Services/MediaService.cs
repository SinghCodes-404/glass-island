using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassIsland.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WpfColor = System.Windows.Media.Color;

namespace GlassIsland.Services;

/// <summary>Now-playing info for any app that reports to Windows media controls (Spotify, browsers, Music…).</summary>
public sealed class MediaService : Observable
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string _title = "Nothing playing", _artist = "Play something to see it here";
    bool _playing, _hasMedia;
    ImageSource? _art;
    WpfColor _artColor = WpfColor.FromRgb(0x9E, 0xA7, 0xFF);
    string _lastTrackKey = "";

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public bool IsPlaying { get => _playing; private set => Set(ref _playing, value); }
    public bool HasMedia { get => _hasMedia; private set => Set(ref _hasMedia, value); }
    public ImageSource? Art { get => _art; private set => Set(ref _art, value); }

    string _sourceApp = "", _sourceName = "";
    /// <summary>AppUserModelID of the app that is playing (e.g. "Spotify.exe", Firefox's install hash).</summary>
    public string SourceApp
    {
        get => _sourceApp;
        private set
        {
            if (!Set(ref _sourceApp, value)) return;
            SourceName = value.Length == 0 ? "" : AppWindows.DisplayName(value);
        }
    }
    /// <summary>Friendly name of the player ("Firefox", "Spotify"), shown on the card.</summary>
    public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }

    /// <summary>Brings the player that is playing to the front. Returns false if no window was found.</summary>
    public bool OpenPlayer() => _sourceApp.Length > 0 && AppWindows.Activate(_sourceApp, _title);
    public WpfColor ArtColor { get => _artColor; private set { if (Set(ref _artColor, value)) Raise(nameof(ArtBrush)); } }
    /// <summary>Album color, lifted toward white so it stays visible on top of colorful glass.</summary>
    public SolidColorBrush ArtBrush => Frozen(new SolidColorBrush(ColorTools.Lighten(_artColor, 0.35)));

    /// <summary>Raised on the UI thread when a new track starts.</summary>
    public event Action? TrackChanged;
    /// <summary>Raised on the UI thread when playback starts or stops.</summary>
    public event Action? PlaybackChanged;

    public Relay TogglePlayCommand { get; }
    public Relay NextCommand { get; }
    public Relay PreviousCommand { get; }

    public MediaService()
    {
        TogglePlayCommand = new Relay(async () => { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } });
        NextCommand = new Relay(async () => { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } });
        PreviousCommand = new Relay(async () => { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } });
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Ui(Attach);
            Attach();
        }
        catch (Exception ex) { App.Log("Media controls unavailable: " + ex.Message); }
    }

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
        }
        _session = _mgr?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnProps;
            _session.PlaybackInfoChanged += OnPlayback;
        }
        _ = RefreshPropsAsync();
        RefreshPlayback();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Ui(() => _ = RefreshPropsAsync());
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Ui(RefreshPlayback);

    void RefreshPlayback()
    {
        bool playing = false;
        try { playing = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            PlaybackChanged?.Invoke();
        }
    }

    async Task RefreshPropsAsync()
    {
        if (_session == null)
        {
            HasMedia = false;
            Title = "Nothing playing";
            Artist = "Play something to see it here";
            Art = null;
            return;
        }
        try
        {
            var p = await _session.TryGetMediaPropertiesAsync();
            if (p == null) return;
            string title = string.IsNullOrWhiteSpace(p.Title) ? "Unknown title" : p.Title;
            string artist = string.IsNullOrWhiteSpace(p.Artist) ? (p.AlbumTitle ?? "") : p.Artist;
            string key = title + "\u0001" + artist;
            Title = title;
            Artist = artist;
            HasMedia = true;

            BitmapSource? art = null;
            if (p.Thumbnail != null)
            {
                using var stream = await p.Thumbnail.OpenReadAsync();
                if (stream.Size > 0 && stream.Size < 8_000_000)
                {
                    var bytes = new byte[stream.Size];
                    using var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                    art = DecodeArt(bytes, out var srcSize);
                    App.Log($"Now playing from '{_session.SourceAppUserModelId}', artwork {srcSize} px");
                }
            }
            // Some players hand Windows tiny artwork (Firefox: 60×60). Keep a sharper copy from an earlier
            // lookup of this same track, otherwise show what we have and try to find a 600 px version.
            bool small = art == null || art.PixelWidth < 300;
            if (small && HdCache.TryGetValue(key, out var hd) && hd != null) art = hd;
            Art = art;
            SourceApp = _session.SourceAppUserModelId ?? "";
            App.Log($"Player: '{SourceName}' → {AppWindows.Describe(SourceApp, title)}");
            ArtColor = ColorTools.Dominant(art) ?? WpfColor.FromRgb(0x9E, 0xA7, 0xFF);
            if (small && !HdCache.ContainsKey(key) && OnlineArtwork()) _ = UpgradeArtAsync(key, title, artist);

            if (key != _lastTrackKey)
            {
                _lastTrackKey = key;
                TrackChanged?.Invoke();
            }
        }
        catch (Exception ex) { App.Log("Media properties failed: " + ex.Message); }
    }

    // ── HD artwork lookup (iTunes Search API) ──

    /// <summary>Whether online artwork lookup is allowed (wired to the setting).</summary>
    public Func<bool> OnlineArtwork { get; set; } = () => true;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    static readonly Dictionary<string, BitmapSource?> HdCache = [];   // per track; null = looked up, nothing found

    /// <summary>
    /// Finds 600×600 artwork for a track when the player only provides a thumbnail. Only the title and artist
    /// are sent, and a result is used only if both clearly match, so the art is never swapped for a wrong one.
    /// </summary>
    async Task UpgradeArtAsync(string key, string title, string artist)
    {
        HdCache[key] = null;
        try
        {
            string t = CleanTitle(title), a = CleanArtist(artist);
            if (t.Length == 0) return;
            // Search with the lead artist only: "A and B" / "A & B" / "A, B" confuses the search.
            string lead = System.Text.RegularExpressions.Regex.Split(a, @"\s+(?:and|x|feat\.?|ft\.?)\s+|\s*[&,]\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)[0].Trim();
            string? artUrl = null;
            foreach (var store in Stores())
            {
                var url = $"https://itunes.apple.com/search?entity=song&limit=10&country={store}&term=" + Uri.EscapeDataString($"{lead} {t}".Trim());
                using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(url));
                foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    string rt = Norm(r.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "");
                    string ra = Norm(r.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "");
                    bool titleOk = rt.Length > 0 && (rt == Norm(t) || rt.StartsWith(Norm(t) + " ") || Norm(t).StartsWith(rt + " "));
                    bool artistOk = a.Length == 0 || Norm(a).Split(' ').Any(w => w.Length >= 3 && ra.Contains(w));
                    if (!titleOk || !artistOk || !r.TryGetProperty("artworkUrl100", out var au)) continue;
                    artUrl = au.GetString()?.Replace("100x100bb", "600x600bb");
                    break;
                }
                if (artUrl != null) break;
            }
            if (artUrl == null) { App.Log($"HD artwork: no confident match for '{title}' by '{artist}'"); return; }

            var img = DecodeArt(await Http.GetByteArrayAsync(artUrl), out var size);
            HdCache[key] = img;
            if (img == null || key != _lastTrackKey) return;   // track changed meanwhile
            Art = img;
            ArtColor = ColorTools.Dominant(img) ?? ArtColor;
            App.Log($"HD artwork: {size} px for '{title}'");
        }
        catch (Exception ex) { App.Log("HD artwork lookup failed: " + ex.Message); }
    }

    /// <summary>
    /// iTunes catalogues differ by country (regional music often exists only in its home store). Try the
    /// country implied by the time zone, the Windows region, then the US store — at most three requests.
    /// </summary>
    static IEnumerable<string> Stores()
    {
        var list = new List<string>();
        void Add(string? c) { if (!string.IsNullOrEmpty(c) && c.Length == 2 && !list.Contains(c.ToUpperInvariant())) list.Add(c.ToUpperInvariant()); }
        // Time zone first: the Windows region is often left at the "United States" default.
        Add(TimeZoneCountry(TimeZoneInfo.Local.Id));
        try { Add(System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName); } catch { }
        Add("US");
        return list.Take(3);
    }

    /// <summary>Country for the time zones where it is unambiguous (keeps this tiny; others fall back to US).</summary>
    static string? TimeZoneCountry(string windowsId) => windowsId switch
    {
        "India Standard Time" => "IN", "Pakistan Standard Time" => "PK", "Bangladesh Standard Time" => "BD",
        "GMT Standard Time" => "GB", "Tokyo Standard Time" => "JP", "Korea Standard Time" => "KR",
        "AUS Eastern Standard Time" => "AU", "Arabian Standard Time" => "AE", "Singapore Standard Time" => "SG",
        "E. South America Standard Time" => "BR", "Central Europe Standard Time" => "DE", "Romance Standard Time" => "FR",
        "Canada Central Standard Time" => "CA", "Nepal Standard Time" => "NP", "Sri Lanka Standard Time" => "LK",
        _ => null
    };

    /// <summary>"Song (Official Video) [4K] ft. X" → "Song".</summary>
    static string CleanTitle(string s)
    {
        var core = System.Text.RegularExpressions.Regex.Replace(s, @"\(.*?\)|\[.*?\]|\s(ft\.?|feat\.?|featuring)\s.*$", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (core.Contains(" - ")) core = core[(core.IndexOf(" - ") + 3)..];   // "Artist - Song" (YouTube style)
        return core.Length > 0 ? core.Trim() : s.Trim();
    }

    static string CleanArtist(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s*-\s*Topic$|VEVO$|Official", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    static string Norm(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant().Replace("&", " and "), @"[^\p{L}\p{N} ]", " ").Trim()
            .Replace("  ", " ");

    /// <summary>
    /// Decodes the artwork at its native resolution (capped at 600 px, enough for the 98 px card on a 4K
    /// display), never upscaled. The UI draws it with high-quality filtering.
    /// </summary>
    static BitmapSource? DecodeArt(byte[] bytes, out string srcSize)
    {
        srcSize = "?";
        try
        {
            int native;
            using (var probe = new MemoryStream(bytes))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                native = frame.PixelWidth;
                srcSize = $"{frame.PixelWidth}×{frame.PixelHeight}";
            }
            var b = new BitmapImage();
            b.BeginInit();
            b.StreamSource = new MemoryStream(bytes);
            if (native > 600) b.DecodePixelWidth = 600;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Fake "now playing" used for design snapshots.</summary>
    public void LoadDemo()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var g = new LinearGradientBrush(WpfColor.FromRgb(0xFF, 0x7A, 0x59), WpfColor.FromRgb(0x6C, 0x3B, 0xFF), 45);
            dc.DrawRectangle(g, null, new Rect(0, 0, 160, 160));
            dc.DrawEllipse(new SolidColorBrush(WpfColor.FromArgb(150, 255, 214, 120)), null, new Point(110, 52), 34, 34);
            dc.DrawRectangle(new SolidColorBrush(WpfColor.FromArgb(120, 20, 10, 60)), null, new Rect(0, 108, 160, 52));
        }
        var rtb = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        Title = "Glass Horizons";
        Artist = "Nova Lane";
        _sourceApp = "demo";
        SourceName = "Spotify";
        HasMedia = true;
        Art = rtb;
        ArtColor = ColorTools.Dominant(rtb) ?? Colors.Orange;
        IsPlaying = true;
        PlaybackChanged?.Invoke();
    }
}
