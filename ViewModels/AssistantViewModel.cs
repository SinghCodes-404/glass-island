using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Media;
using GlassIsland.Core;
using GlassIsland.Services;

namespace GlassIsland.ViewModels;

public sealed class ChatMessage(bool isUser, string text) : Observable
{
    string _text = text;
    bool _streaming, _error;

    public bool IsUser { get; } = isUser;
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsStreaming { get => _streaming; set => Set(ref _streaming, value); }
    public bool IsError { get => _error; set => Set(ref _error, value); }
    /// <summary>What the assistant actually did ("Dark mode on", "Wallpaper: Space Tacos!").</summary>
    public ObservableCollection<string> Actions { get; } = [];

    public void Append(string delta) => Text += delta;
}

/// <summary>Gemini assistant: chat state, key setup, and the tools that let it operate the island.</summary>
public sealed class AssistantViewModel : Observable
{
    readonly MainViewModel _root;
    readonly GeminiAgent _agent = new();
    readonly JsonArray _tools = BuildTools();
    CancellationTokenSource? _cts;
    bool _hasKey = SecretStore.HasKey, _busy, _validating;
    string _input = "", _keyStatus = "";

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public IReadOnlyList<string> Suggestions { get; } =
        ["Turn on dark mode", "Shuffle a calm wallpaper", "Start a 25-minute focus session", "What's playing right now?"];

    public bool HasKey { get => _hasKey; private set => Set(ref _hasKey, value); }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public bool IsValidating { get => _validating; private set => Set(ref _validating, value); }
    public string Input { get => _input; set => Set(ref _input, value ?? ""); }
    public string KeyStatus { get => _keyStatus; private set => Set(ref _keyStatus, value); }
    public string ModelLabel => _root.Settings.AssistantModel switch
    {
        "gemini-3.8-flash" => "Gemini 3.8 Flash",
        var m => m,
    };

    public Relay SendCommand { get; }
    public Relay StopCommand { get; }
    public Relay NewChatCommand { get; }
    public Relay ForgetKeyCommand { get; }
    public Relay GetKeyCommand { get; }

    public AssistantViewModel(MainViewModel root)
    {
        _root = root;
        _agent.Model = root.Settings.AssistantModel;
        SendCommand = new Relay(p => Send(p as string ?? Input));
        StopCommand = new Relay(() => _cts?.Cancel());
        NewChatCommand = new Relay(() =>
        {
            _cts?.Cancel();
            Messages.Clear();
            _agent.Reset();
        });
        ForgetKeyCommand = new Relay(() =>
        {
            SecretStore.Delete();
            HasKey = false;
            KeyStatus = "";
        });
        GetKeyCommand = new Relay(() =>
        {
            try { Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }); } catch { }
        });
    }

    /// <summary>Validates the key with Google, then stores it encrypted (DPAPI). The key never goes anywhere else.</summary>
    public async Task SaveKeyAsync(string key)
    {
        key = key.Trim();
        if (key.Length < 20) { KeyStatus = "That doesn't look like a Gemini API key."; return; }
        IsValidating = true;
        KeyStatus = "Checking the key with Google…";
        var problem = await GeminiAgent.ValidateKeyAsync(key);
        IsValidating = false;
        if (problem != null) { KeyStatus = problem; return; }
        SecretStore.Save(key);
        KeyStatus = "";
        HasKey = true;
    }

    public async void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || IsBusy) return;
        var key = SecretStore.Load();
        if (key == null) { HasKey = false; return; }

        Messages.Add(new ChatMessage(true, text));
        Input = "";
        var reply = new ChatMessage(false, "") { IsStreaming = true };
        Messages.Add(reply);
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await _agent.RunAsync(key, text, SystemPrompt(), _tools, call => ExecuteAsync(call, reply), reply.Append, _cts.Token);
            if (reply.Text.Length == 0) reply.Text = reply.Actions.Count > 0 ? "Done." : "…";
        }
        catch (OperationCanceledException) { if (reply.Text.Length == 0) reply.Text = "Stopped."; }
        catch (GeminiException ex)
        {
            reply.IsError = true;
            reply.Text = ex.Status switch
            {
                400 when ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) => "Your API key was rejected. " + ex.Message,
                401 or 403 => "Your API key was rejected. " + ex.Message,
                429 => "Gemini's rate limit was reached. Try again in a moment.",
                503 => "Gemini is overloaded right now (all fast models are busy). Try again in a minute.",
                _ => ex.Message,
            };
            if (ex.Status == 400 && ex.Message.Contains("interaction", StringComparison.OrdinalIgnoreCase)) _agent.Reset();
            App.Log($"Gemini error {ex.Status}: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException)
        {
            reply.IsError = true;
            reply.Text = "Couldn't reach Gemini. Check your connection. (" + ex.Message + ")";
        }
        finally
        {
            reply.IsStreaming = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    string SystemPrompt() =>
        "You are the assistant inside Glass Island, a Dynamic Island-style control centre at the top of the user's " +
        "Windows PC. Be brief and friendly: one to three sentences unless asked for more. Write plain text; you may " +
        "use **bold** and simple '- ' bullet lists, but no headings or tables. When the user asks you to change something " +
        "(wallpaper, light/dark theme, accent colour, music, volume, focus timer, tasks), use the tools, then confirm what " +
        "you did in one short sentence. Use get_status when you need the current state. For a wallpaper by theme or mood, " +
        "search_wallpapers first and pick the best match. For anything outside these tools, answer helpfully as a general " +
        $"assistant. The local time is {DateTime.Now:dddd d MMMM yyyy, HH:mm}.";

    // ───────────────────────── tools ─────────────────────────

    static JsonObject Fn(string name, string description, JsonObject? properties = null, params string[] required)
    {
        var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties ?? [] };
        if (required.Length > 0) parameters["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters };
    }

    static JsonObject P(string type, string description, params string[] options)
    {
        var o = new JsonObject { ["type"] = type, ["description"] = description };
        if (options.Length > 0) o["enum"] = new JsonArray(options.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        return o;
    }

    static JsonArray BuildTools() =>
    [
        Fn("get_status", "Current state of the PC/island: time, now playing, theme, accent colour, volume, current wallpaper, focus timer and open tasks."),
        Fn("search_wallpapers", "Search the user's Wallpaper Engine library by words in the title or tags (e.g. 'rain', 'anime city', 'space').",
            new JsonObject { ["query"] = P("string", "Words to search for"), ["limit"] = P("integer", "Max results, default 8") }, "query"),
        Fn("apply_wallpaper", "Apply a wallpaper by its id (from search_wallpapers).", new JsonObject { ["id"] = P("string", "Wallpaper id") }, "id"),
        Fn("shuffle_wallpaper", "Apply a random wallpaper, optionally only among those matching a search query.",
            new JsonObject { ["query"] = P("string", "Optional words to restrict the random pick") }),
        Fn("set_wallpaper_playback", "Pause/resume the animated wallpaper and/or mute/unmute its sound.",
            new JsonObject { ["paused"] = P("boolean", "true to pause"), ["muted"] = P("boolean", "true to mute") }),
        Fn("set_theme", "Switch Windows between light and dark mode, or automatic (light by day, dark at night).",
            new JsonObject { ["mode"] = P("string", "Theme mode", "light", "dark", "auto") }, "mode"),
        Fn("set_accent_color", "Set the Windows accent colour, or make it follow the wallpaper.",
            new JsonObject { ["color"] = P("string", "Hex colour like #4F8CFF (omit when match_wallpaper is true)"), ["match_wallpaper"] = P("boolean", "true to follow the wallpaper's colours") }),
        Fn("media_control", "Control the music/video that is playing.",
            new JsonObject { ["action"] = P("string", "What to do", "play_pause", "next", "previous") }, "action"),
        Fn("set_volume", "Set the system volume and/or mute.",
            new JsonObject { ["percent"] = P("integer", "Volume 0-100"), ["muted"] = P("boolean", "true to mute") }),
        Fn("start_focus", "Start a focus (Pomodoro) session.", new JsonObject { ["minutes"] = P("integer", "Length in minutes, default 25") }),
        Fn("stop_focus", "Stop and reset the focus session."),
        Fn("add_task", "Add a task to the user's task list.", new JsonObject { ["text"] = P("string", "The task") }, "text"),
        Fn("complete_task", "Mark the first open task that contains this text as done.", new JsonObject { ["text"] = P("string", "Part of the task's text") }, "text"),
        Fn("list_tasks", "List the user's tasks."),
    ];

    async Task<JsonNode?> ExecuteAsync(ToolCall call, ChatMessage reply)
    {
        var a = call.Args;
        // Models sometimes send numbers/booleans as strings: accept both.
        string? S(string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[k]?.ToString();
        bool? B(string k)
        {
            if (a[k] is not JsonValue v) return null;
            if (v.TryGetValue<bool>(out var b)) return b;
            return v.TryGetValue<string>(out var s) && bool.TryParse(s, out b) ? b : null;
        }
        int? I(string k)
        {
            if (a[k] is not JsonValue v) return null;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            return v.TryGetValue<string>(out var s) && int.TryParse(s, out i) ? i : null;
        }
        void Did(string what) => reply.Actions.Add(what);

        var walls = _root.Walls;
        var theme = _root.Theme;
        var focus = _root.Focus;
        var media = _root.Media;

        switch (call.Name)
        {
            case "get_status":
                await walls.EnsureLoadedAsync();   // the current wallpaper is only known once the library is indexed
                walls.RefreshState();
                return new JsonObject
                {
                    ["time"] = DateTime.Now.ToString("dddd d MMM yyyy HH:mm"),
                    ["now_playing"] = media.HasMedia
                        ? new JsonObject { ["title"] = media.Title, ["artist"] = media.Artist, ["app"] = media.SourceName, ["playing"] = media.IsPlaying }
                        : null,
                    ["theme"] = theme.Mode.ToLowerInvariant(),
                    ["accent_color"] = theme.Accent.ToString().Remove(1, 2),
                    ["accent_follows_wallpaper"] = theme.MatchWallpaper,
                    ["volume_percent"] = _root.Volume,
                    ["muted"] = _root.IsMuted,
                    ["wallpaper"] = walls.CurrentTitle,
                    ["wallpaper_paused"] = walls.IsPaused,
                    ["focus"] = focus.IsActive ? new JsonObject { ["running"] = focus.IsRunning, ["remaining"] = focus.RemainingText } : null,
                    ["open_tasks"] = new JsonArray(focus.Todos.Where(t => !t.Done).Select(t => (JsonNode)JsonValue.Create(t.Text)!).ToArray()),
                };

            case "search_wallpapers":
            {
                await walls.EnsureLoadedAsync();
                var found = walls.SearchItems(S("query") ?? "").Take(Math.Clamp(I("limit") ?? 8, 1, 20));
                return new JsonObject
                {
                    ["results"] = new JsonArray(found.Select(w => (JsonNode)new JsonObject
                    {
                        ["id"] = w.Id, ["title"] = w.Title, ["type"] = w.TypeLabel, ["favorite"] = w.IsFavorite, ["current"] = w.IsCurrent,
                    }).ToArray()),
                };
            }

            case "apply_wallpaper":
            {
                await walls.EnsureLoadedAsync();
                var w = walls.All.FirstOrDefault(x => x.Id == S("id"));
                if (w == null) return new JsonObject { ["error"] = "No wallpaper with that id. Use search_wallpapers first." };
                walls.Apply(w);
                Did("Wallpaper: " + w.Title);
                return new JsonObject { ["applied"] = w.Title };
            }

            case "shuffle_wallpaper":
            {
                await walls.EnsureLoadedAsync();
                var q = S("query");
                var pool = (string.IsNullOrWhiteSpace(q) ? walls.All : walls.SearchItems(q)).Where(x => !x.IsCurrent).ToList();
                if (pool.Count == 0) return new JsonObject { ["error"] = "No wallpapers match." };
                var w = pool[Random.Shared.Next(pool.Count)];
                walls.Apply(w);
                Did("Wallpaper: " + w.Title);
                return new JsonObject { ["applied"] = w.Title };
            }

            case "set_wallpaper_playback":
            {
                if (B("paused") is { } paused) { walls.SetPaused(paused); Did(paused ? "Wallpaper paused" : "Wallpaper playing"); }
                if (B("muted") is { } muted) { walls.SetMuted(muted); Did(muted ? "Wallpaper muted" : "Wallpaper sound on"); }
                return new JsonObject { ["ok"] = true };
            }

            case "set_theme":
            {
                var mode = (S("mode") ?? "dark").ToLowerInvariant();
                theme.ModeCommand.Execute(mode switch { "light" => "Light", "auto" => "Auto", _ => "Dark" });
                Did(mode switch { "light" => "Light mode", "auto" => "Auto theme", _ => "Dark mode" });
                return new JsonObject { ["theme"] = theme.Mode.ToLowerInvariant() };
            }

            case "set_accent_color":
            {
                if (B("match_wallpaper") == true)
                {
                    theme.MatchWallpaper = true;
                    Did("Accent follows wallpaper");
                    return new JsonObject { ["ok"] = true };
                }
                if (S("color") is not { } hex) return new JsonObject { ["error"] = "Give a hex colour or match_wallpaper." };
                Color c;
                try { c = (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex); }
                catch { return new JsonObject { ["error"] = "Not a valid colour: " + hex }; }
                theme.MatchWallpaper = false;
                theme.ApplyAccent(Color.FromRgb(c.R, c.G, c.B));
                Did($"Accent #{c.R:X2}{c.G:X2}{c.B:X2}");
                return new JsonObject { ["ok"] = true };
            }

            case "media_control":
            {
                if (!media.HasMedia) return new JsonObject { ["error"] = "Nothing is playing." };
                switch (S("action"))
                {
                    case "next": media.NextCommand.Execute(null); Did("Next track"); break;
                    case "previous": media.PreviousCommand.Execute(null); Did("Previous track"); break;
                    default: media.TogglePlayCommand.Execute(null); Did(media.IsPlaying ? "Paused" : "Playing"); break;
                }
                return new JsonObject { ["ok"] = true };
            }

            case "set_volume":
            {
                _root.RefreshAudio();
                if (I("percent") is { } p) { _root.Volume = Math.Clamp(p, 0, 100); Did($"Volume {_root.Volume}%"); }
                if (B("muted") is { } m) { _root.IsMuted = m; Did(m ? "Muted" : "Unmuted"); }
                return new JsonObject { ["volume_percent"] = _root.Volume, ["muted"] = _root.IsMuted };
            }

            case "start_focus":
            {
                if (I("minutes") is { } min && min is > 0 and <= 240) focus.PresetCommand.Execute(min);
                focus.Start();
                Did($"Focus {focus.Minutes} min");
                return new JsonObject { ["minutes"] = focus.Minutes };
            }

            case "stop_focus":
                focus.ResetCommand.Execute(null);
                Did("Focus stopped");
                return new JsonObject { ["ok"] = true };

            case "add_task":
            {
                var text = S("text")?.Trim();
                if (string.IsNullOrEmpty(text)) return new JsonObject { ["error"] = "Empty task." };
                focus.NewTodo = text;
                focus.AddTodoCommand.Execute(null);
                Did("Task added");
                return new JsonObject { ["added"] = text };
            }

            case "complete_task":
            {
                var q = S("text") ?? "";
                var t = focus.Todos.FirstOrDefault(x => !x.Done && x.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase));
                if (t == null) return new JsonObject { ["error"] = "No open task contains that text." };
                t.Done = true;
                Did("Task done");
                return new JsonObject { ["completed"] = t.Text };
            }

            case "list_tasks":
                return new JsonObject
                {
                    ["tasks"] = new JsonArray(focus.Todos.Select(t => (JsonNode)new JsonObject { ["text"] = t.Text, ["done"] = t.Done }).ToArray()),
                };

            default:
                return new JsonObject { ["error"] = "Unknown tool " + call.Name };
        }
    }

    /// <summary>A fake conversation for design snapshots.</summary>
    public void LoadDemo()
    {
        HasKey = true;
        Messages.Add(new ChatMessage(true, "Make it cozy: something rainy and dark mode"));
        var r = new ChatMessage(false, "Switched to **dark mode** and put on *Rainy Window Café*. Want me to start a focus session too?");
        r.Actions.Add("Dark mode");
        r.Actions.Add("Wallpaper: Rainy Window Café");
        Messages.Add(r);
        Messages.Add(new ChatMessage(true, "Yes, 50 minutes, and add 'finish DBMS assignment' to my tasks"));
        var r2 = new ChatMessage(false, "Done: a 50-minute focus session is running and the task is on your list. Good luck!");
        r2.Actions.Add("Focus 50 min");
        r2.Actions.Add("Task added");
        Messages.Add(r2);
    }
}
