using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassIsland.Core;
using GlassIsland.Services;
using GlassIsland.ViewModels;
using static GlassIsland.Core.Native;

namespace GlassIsland;

public partial class MainWindow : Window
{
    const double TopMargin = 8, TuckedTop = 3;
    const double ExpandedWinW = 760, ExpandedWinH = 456;
    const double ExpandedW = 680;

    readonly MainViewModel _vm = new();
    readonly BackdropCapture _capture = new();
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    readonly DispatcherTimer _fullscreenTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _trimTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _tuckTimer = new(DispatcherPriority.Background);

    // Geometry is driven by springs; frames are only rendered while one of them is moving.
    readonly Spring _w = new(176, epsilon: 0.15), _h = new(34, epsilon: 0.15), _r = new(17, epsilon: 0.15);
    readonly Spring _tabX = new(0, 420, 34, 0.15), _tabW = new(80, 420, 34, 0.15);
    readonly Spring _tint = new(0.42, 70, 17, 0.002);
    readonly Spring _y = new(TopMargin, 320, 31, 0.05);   // distance from the top edge (smaller when tucked)

    IntPtr _hwnd;
    double _dpi = 1, _winW = ExpandedWinW, _winH = ExpandedWinH;
    Int32Rect _screenRect;
    bool _expanded, _hovering, _toastActive, _animating, _keyboardMode, _hiddenForFullscreen, _instant;
    bool _visualizerRunning, _scrolling, _userAway, _tucked;
    string _tab = "Home";
    IntPtr _prevForeground;
    TimeSpan _lastFrame;
    int _traceFrames;
    readonly Storyboard _visualizer;
    ScrollViewer? _wallScroll;
    double _scrollTarget;
    readonly List<Button> _monitorChips = [], _presetChips = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _visualizer = (Storyboard)Resources["Visualizer"];
        foreach (var p in new FrameworkElement[] { HomePanel, WallPanel, ThemePanel, FocusPanel }) p.RenderTransform = new TranslateTransform();

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_expanded && !_keyboardMode && !Island.IsMouseOver && Mouse.LeftButton == MouseButtonState.Released) Collapse();
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); EndToast(); };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _fullscreenTimer.Interval = TimeSpan.FromSeconds(1);
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); TrimMemory(); };
        _tuckTimer.Tick += (_, _) =>
        {
            _tuckTimer.Stop();
            // Trust the real cursor position: a "mouse left" event isn't always delivered (e.g. the cursor jumps).
            bool near = MouseNearIsland();
            if (!near && _hovering) { _hovering = false; UpdatePillLabels(); }
            if (_expanded || _toastActive || near) ScheduleTuck();
            else Tuck();
        };
        _vm.Theme.IslandBehaviorChanged += () =>
        {
            if (!_vm.Settings.HideInFullscreen && _hiddenForFullscreen) ShowFromFullscreen();
            if (_tucked) { Retarget(); StartAnimating(); UpdateLine(); }
            ScheduleTuck();
        };

        _vm.Toast += (title, sub, image, glyph) => ShowToast(title, sub, image, glyph, image != null ? "wallpaper" : "info");
        _vm.Media.PlaybackChanged += UpdatePill;
        _vm.Media.TrackChanged += () =>
        {
            if (!_expanded && _vm.Media.IsPlaying) ShowToast(_vm.Media.Title, _vm.Media.Artist, _vm.Media.Art, "", "music");
        };
        _vm.Focus.RunningChanged += UpdatePill;
        _vm.Focus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FocusViewModel.IsActive)) UpdatePill();
            if (e.PropertyName == nameof(FocusViewModel.Minutes)) UpdateChips();
            if (e.PropertyName == nameof(FocusViewModel.Progress) && _tucked) UpdateLine();
        };
        _vm.Focus.Finished += () => ShowToast("Focus session complete", "Nice work, take a short break", null, "");
        _vm.Focus.Todos.CollectionChanged += (_, _) => UpdateTodoEmpty();
        _vm.Theme.GlassModeChanged += ApplyGlassMode;
        _vm.Walls.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WallpapersViewModel.Rows) or nameof(WallpapersViewModel.IsLoading)) UpdateWallEmpty();
            if (e.PropertyName == nameof(WallpapersViewModel.Monitor)) UpdateChips();
        };
        UpdateTodoEmpty();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            if (!_keyboardMode) return;
            ExitKeyboard(restoreFocus: false);
            if (_expanded && !Island.IsMouseOver) Collapse();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _expanded) { Collapse(); e.Handled = true; } };
        SearchBox.PreviewMouseLeftButtonDown += (_, _) => EnterKeyboard();
        TodoBox.PreviewMouseLeftButtonDown += (_, _) => EnterKeyboard();
        WallList.PreviewMouseWheel += SmoothWheel;
    }

    // ───────────────────────────── window plumbing ─────────────────────────────

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Per-pixel transparency through DWM instead of WPF's layered-window mode. Layered windows force a
        // GPU→CPU read-back of every frame; this path stays on the GPU, which is what makes animation smooth.
        var src = HwndSource.FromHwnd(_hwnd)!;
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);
        SetWindowLong(_hwnd, GWL_STYLE, WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | (GetWindowLong(_hwnd, GWL_STYLE) & WS_VISIBLE));
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        int noRound = DWMWCP_DONOTROUND, noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        StartCaptureWorker();
        if (!App.Scripted) RegisterFirstFreeHotkey();
        ApplyGlassMode();
        PlaceWindow();
    }

    /// <summary>Tries a few global shortcuts and keeps the first one no other app owns.</summary>
    void RegisterFirstFreeHotkey()
    {
        (uint Mods, uint Key, string Label)[] candidates =
        [
            (MOD_CONTROL | MOD_ALT, VK_SPACE, "Ctrl + Alt + Space"),
            (MOD_ALT, VK_OEM_3, "Alt + `"),
            (MOD_CONTROL | MOD_ALT, VK_I, "Ctrl + Alt + I"),
            (MOD_CONTROL | MOD_SHIFT, VK_SPACE, "Ctrl + Shift + Space"),
        ];
        foreach (var (mods, key, label) in candidates)
        {
            if (!RegisterHotKey(_hwnd, 1, mods | MOD_NOREPEAT, key)) continue;
            _vm.Hotkey = label;
            App.Log("Hotkey: " + label);
            return;
        }
        _vm.Hotkey = "No free shortcut";
        App.Log("All hotkey candidates are taken by other apps.");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                handled = true;
                Trace($"hotkey expanded={_expanded}");
                if (_expanded) Collapse();
                else
                {
                    if (_hiddenForFullscreen) ShowFromFullscreen();
                    Expand();
                }
                break;
            case WM_MOUSEACTIVATE when !_keyboardMode:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Dispatcher.BeginInvoke(PlaceWindow, DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Centers the window at the top of the primary monitor. It keeps its full size forever (resizing a GPU
    /// surface is slow); what is visible and clickable is controlled by the window region instead.
    /// </summary>
    void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        _winW = ExpandedWinW;
        _winH = ExpandedWinH;
        var mon = PrimaryMonitorRect();
        _dpi = PrimaryDpi() / 96.0;
        int w = (int)Math.Round(_winW * _dpi), h = (int)Math.Round(_winH * _dpi);
        int x = mon.Left + (mon.Width - w) / 2, y = mon.Top;
        _screenRect = new Int32Rect(x, y, w, h);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
        Root.Width = _winW;
        Root.Height = _winH;
        _regionRect = Rect.Empty;
        ApplyGeometry();
        UpdateRegion(settled: true);
        UpdateCaptureArea();
    }

    // ── window region: only the island (plus room for its shadow) is visible and catches the mouse ──

    Rect _regionRect = Rect.Empty;

    Rect IslandBounds(double w, double h, double y) => new((_winW - w) / 2, y, w, h);

    /// <summary>
    /// While animating, the region only grows (to cover both the current and the target shape, so a frame is
    /// never clipped); once settled it shrinks to fit. That is two region updates per transition, not one per frame.
    /// </summary>
    void UpdateRegion(bool settled)
    {
        if (_hwnd == IntPtr.Zero) return;
        var r = IslandBounds(_w.Target, _h.Target, _y.Target);
        if (!settled) r.Union(IslandBounds(_w.Value, _h.Value, _y.Value));
        if (_tucked) r.Union(new Rect((_winW - HotZone.Width) / 2, 0, HotZone.Width, HotZone.Height));
        // room for the soft shadow and spring overshoot
        r = new Rect(Math.Max(0, r.X - 18), 0, Math.Min(_winW, r.Width + 36), Math.Min(_winH, r.Bottom + 26));
        if (!settled && !_regionRect.IsEmpty) r.Union(_regionRect);
        if (r == _regionRect) return;
        _regionRect = r;
        var rgn = CreateRectRgn((int)Math.Floor(r.X * _dpi), (int)Math.Floor(r.Y * _dpi), (int)Math.Ceiling(r.Right * _dpi), (int)Math.Ceiling(r.Bottom * _dpi));
        SetWindowRgn(_hwnd, rgn, true); // the system owns rgn from here on
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyGeometry();
        UpdatePill();
        CaptureBackdrop();
        RestartCaptureTimer();
        _fullscreenTimer.Start();

        if (App.SnapshotDir != null)
        {
            AppSettings.ReadOnly = true;
            if (!App.Demo) await _vm.Media.InitAsync();
            await RunSnapshots(App.SnapshotDir);
            return;
        }
        if (App.BenchFile != null)
        {
            AppSettings.ReadOnly = true;
            var target = (HwndTarget)PresentationSource.FromVisual(this)!.CompositionTarget;
            App.Log($"render tier={RenderCapability.Tier >> 16} renderMode={target.RenderMode} processMode={RenderOptions.ProcessRenderMode}");
            await _vm.Media.InitAsync();
            await RunBench(App.BenchFile);
            return;
        }
        await _vm.Media.InitAsync();
        ScheduleTuck();
        // Index wallpapers quietly in the background so the first open of the tab is instant.
        await Task.Delay(TimeSpan.FromSeconds(6));
        await _vm.Walls.EnsureLoadedAsync();
        WarmUpPanels();
        // Startup work is done: hand the one-off memory (JSON parsing, JIT, decode buffers) back to Windows.
        if (!_expanded) TrimMemory();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        Trace("closed");
        _closing = true;
        _captureKick.Set();
        UnregisterHotKey(_hwnd, 1);
        _vm.Settings.SaveNow();
        _capture.Dispose();
    }

    // ───────────────────────────── geometry & springs ─────────────────────────────

    double PanelHeight(string tab) => tab switch { "Wallpapers" => 396, "Theme" => 356, "Focus" => 270, _ => 212 };

    string PillMode() => _vm.Focus.IsActive ? "timer" : _vm.Media.IsPlaying ? "music" : "idle";

    void Retarget()
    {
        double w, h;
        _y.Target = _tucked && !_expanded && !_toastActive ? TuckedTop : TopMargin;
        if (_expanded) { w = ExpandedW; h = PanelHeight(_tab); _r.Target = 32; }
        else if (_toastActive) { w = 340; h = 48; _r.Target = 24; }
        else if (_tucked) { w = 72; h = 5; _r.Target = 2.5; }
        else
        {
            (w, h) = (PillMode(), _hovering) switch
            {
                ("music", false) => (204.0, 34.0),
                ("music", true) => (300.0, 40.0),
                ("timer", false) => (176.0, 34.0),
                ("timer", true) => (232.0, 40.0),
                (_, false) => (176.0, 34.0),
                (_, true) => (196.0, 40.0),
            };
            _r.Target = h / 2;
        }
        _w.Target = w;
        _h.Target = h;
    }

    void SetSpringFeel(bool bouncy)
    {
        foreach (var s in new[] { _w, _h, _r })
        {
            s.Stiffness = bouncy ? 230 : 320;
            s.Damping = bouncy ? 21 : 31;
        }
    }

    void StartAnimating()
    {
        UpdateRegion(settled: false);
        if (_animating) return;
        _animating = true;
        _lastFrame = default;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastFrame) return;
        double dt = _lastFrame == default ? 1 / 60.0 : Math.Min((t - _lastFrame).TotalSeconds, 1 / 24.0);
        _lastFrame = t;
        bool moving = _w.Step(dt) | _h.Step(dt) | _r.Step(dt) | _tabX.Step(dt) | _tabW.Step(dt) | _tint.Step(dt) | _y.Step(dt);
        if (Tracing && ++_traceFrames % 120 == 0)
            App.Log($"frame w={_w.Value:F2}/{_w.Target} h={_h.Value:F2}/{_h.Target} r={_r.Value:F2}/{_r.Target} tx={_tabX.Value:F2}/{_tabX.Target} tw={_tabW.Value:F2}/{_tabW.Target} tint={_tint.Value:F4}/{_tint.Target:F4}");
        ApplyGeometry();
        if (moving) return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        OnSettled();
    }

    /// <summary>Diagnostics: set GI_TRACE=1 to log state transitions to %APPDATA%\GlassIsland\log.txt.</summary>
    static readonly bool Tracing = Environment.GetEnvironmentVariable("GI_TRACE") == "1";

    static void Trace(string msg)
    {
        if (Tracing) App.Log(msg);
    }

    void OnSettled()
    {
        Trace($"settled expanded={_expanded} w={_w.Value} h={_h.Value}");
        UpdateRegion(settled: true);
        UpdateCaptureArea();
    }

    void SnapAll()
    {
        foreach (var s in new[] { _w, _h, _r, _tabX, _tabW, _tint, _y }) s.Snap();
        ApplyGeometry();
        OnSettled();
    }

    void ApplyGeometry()
    {
        double W = Math.Max(8, _w.Value), H = Math.Max(3, _h.Value), R = Math.Clamp(_r.Value, 0, H / 2), Y = _y.Value;
        Island.Width = W;
        Island.Height = H;
        Island.Margin = new Thickness(0, Y, 0, 0);
        ShadowBox.Margin = new Thickness(0, Y + 7, 0, 0);
        IslandClip.Rect = new Rect(0, 0, W, H);
        IslandClip.RadiusX = IslandClip.RadiusY = R;
        var cr = new CornerRadius(R);
        Tint.CornerRadius = Sheen.CornerRadius = InnerGlow.CornerRadius = Rim.CornerRadius = Shadow1.CornerRadius = cr;
        Shadow2.CornerRadius = new CornerRadius(R + 5);
        Shadow3.CornerRadius = new CornerRadius(R + 10);
        ShadowBox.Width = Math.Max(10, W - 18);
        ShadowBox.Height = Math.Max(4, H - 4);

        // The backdrop image lives in window coordinates so the glass stays registered with the screen behind it.
        double left = (_winW - W) / 2;
        Canvas.SetLeft(BackdropImg, -left);
        Canvas.SetTop(BackdropImg, -Y);
        BackdropImg.Width = _winW;
        BackdropImg.Height = _winH;
        LensScale.CenterX = _winW / 2;
        LensScale.CenterY = Y + H / 2;

        CollapsedLayer.Width = W; CollapsedLayer.Height = H;
        ToastLayer.Width = W; ToastLayer.Height = H;
        LineLayer.Width = W; LineLayer.Height = H;
        LineFill.Width = W * _lineFill;
        Canvas.SetLeft(ExpandedLayer, (W - ExpandedW) / 2);

        Tint.Opacity = _tint.Value;
        TabIndicatorX.X = _tabX.Value;
        TabIndicator.Width = Math.Max(10, _tabW.Value);
        ComposeGlass();
    }

    // ───────────────────────────── states ─────────────────────────────

    void Expand(string? tab = null)
    {
        _collapseTimer.Stop();
        _trimTimer.Stop();
        if (_expanded)
        {
            if (tab != null) SelectTab(tab);
            return;
        }
        _expanded = true;
        _tuckTimer.Stop();
        LeaveTuck();
        if (_toastActive)
        {
            _toastTimer.Stop();
            _toastActive = false;
            Fade(ToastLayer, 0, 90, 0, () => !_toastActive);
        }
        StopVisualizer();
        RestartCaptureTimer();
        _vm.RefreshAudio();
        _vm.Walls.RefreshState();

        ExpandedLayer.Visibility = Visibility.Visible;
        ExpandedLayer.UpdateLayout();
        SelectTab(tab ?? _tab, animate: false);

        SetSpringFeel(bouncy: true);
        Retarget();
        StartAnimating();

        Fade(CollapsedLayer, 0, 90, 0, () => _expanded);
        if (_instant)
        {
            ExpandedLayer.BeginAnimation(OpacityProperty, null);
            ExpandedLayer.Opacity = 1;
            ExpandScale.ScaleX = ExpandScale.ScaleY = 1;
            return;
        }
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        ExpandedLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)) { BeginTime = Ms(70), EasingFunction = ease });
        ExpandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, Ms(460)) { EasingFunction = ease });
        ExpandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, Ms(460)) { EasingFunction = ease });
    }

    void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _collapseTimer.Stop();
        StopScroll();
        ExitKeyboard(restoreFocus: true);
        SetSpringFeel(bouncy: false);
        _hovering = false;
        Retarget();
        StartAnimating();

        Fade(ExpandedLayer, 0, 110, 0, () => !_expanded);
        if (!_instant)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            ExpandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, Ms(160)) { EasingFunction = ease });
            ExpandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, Ms(160)) { EasingFunction = ease });
        }
        Fade(CollapsedLayer, 1, 220, 110);
        UpdatePill();
        RestartCaptureTimer();
        _trimTimer.Stop();   // re-arm: trim 30 s after the *last* close
        _trimTimer.Start();
        ScheduleTuck();
    }

    void SelectTab(string tab, bool animate = true)
    {
        string old = _tab;
        _tab = tab;
        if (tab != "Wallpapers") StopScroll();
        TabHome.Tag = tab == "Home";
        TabWalls.Tag = tab == "Wallpapers";
        TabTheme.Tag = tab == "Theme";
        TabFocus.Tag = tab == "Focus";

        var panels = new (string Name, FrameworkElement El)[] { ("Home", HomePanel), ("Wallpapers", WallPanel), ("Theme", ThemePanel), ("Focus", FocusPanel) };
        int oldIndex = Array.FindIndex(panels, p => p.Name == old), newIndex = Array.FindIndex(panels, p => p.Name == tab);
        foreach (var (name, el) in panels)
        {
            if (name == tab)
            {
                el.Visibility = Visibility.Visible;
                if (animate && old != tab && !_instant)
                {
                    var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
                    el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)) { BeginTime = Ms(40), EasingFunction = ease });
                    ((TranslateTransform)el.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(newIndex > oldIndex ? 18 : -18, 0, Ms(380)) { EasingFunction = ease });
                }
                else
                {
                    el.BeginAnimation(OpacityProperty, null);
                    el.Opacity = 1;
                }
            }
            else el.Visibility = Visibility.Collapsed;
        }

        ExpandedLayer.Height = PanelHeight(tab);
        if (tab == "Wallpapers") _ = _vm.Walls.EnsureLoadedAsync();
        if (tab == "Focus" || tab == "Wallpapers") UpdateChips();

        MoveIndicator(animate && !_instant);
        Retarget();
        StartAnimating();
    }

    /// <summary>
    /// Builds every panel's visual tree once while nobody is looking (invisible, clipped away), so the first
    /// open of a tab doesn't spend a frame creating ~40 wallpaper tiles and templates.
    /// </summary>
    void WarmUpPanels()
    {
        if (_expanded) return;
        ExpandedLayer.BeginAnimation(OpacityProperty, null);
        ExpandedLayer.Opacity = 0;
        ExpandedLayer.Visibility = Visibility.Visible;
        foreach (var panel in new FrameworkElement[] { HomePanel, WallPanel, ThemePanel, FocusPanel })
        {
            panel.Visibility = Visibility.Visible;
            ExpandedLayer.UpdateLayout();
            panel.Visibility = Visibility.Collapsed;
        }
        ExpandedLayer.Visibility = Visibility.Collapsed;
        SelectTab(_tab, animate: false);
    }

    void MoveIndicator(bool animate)
    {
        var btn = _tab switch { "Wallpapers" => TabWalls, "Theme" => TabTheme, "Focus" => TabFocus, _ => TabHome };
        if (btn.ActualWidth <= 0) TabBar.UpdateLayout();
        var p = btn.TranslatePoint(new Point(0, 0), TabBar);
        _tabX.Target = p.X;
        _tabW.Target = btn.ActualWidth;
        if (!animate) { _tabX.Snap(); _tabW.Snap(); ApplyGeometry(); }
        else StartAnimating();
    }

    void UpdatePill()
    {
        var mode = PillMode();
        void Show(FrameworkElement el, bool on)
        {
            if (on && el.Visibility != Visibility.Visible)
            {
                el.Visibility = Visibility.Visible;
                if (!_instant) el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)) { BeginTime = Ms(80) });
            }
            else if (!on) el.Visibility = Visibility.Collapsed;
        }
        Show(PillIdle, mode == "idle");
        Show(PillMusic, mode == "music");
        Show(PillTimer, mode == "timer");

        if (mode == "music" && !_expanded && !_hiddenForFullscreen && !_userAway && !_toastActive && !_tucked) StartVisualizer(); else StopVisualizer();
        UpdatePillLabels();
        UpdateLine();
        if (!_expanded)
        {
            SetSpringFeel(bouncy: true);
            Retarget();
            StartAnimating();
        }
    }

    void UpdatePillLabels()
    {
        Fade(PillTitle, _hovering ? 1 : 0, 180, _hovering ? 90 : 0);
        Fade(PillTimerTitle, _hovering ? 1 : 0, 180, _hovering ? 90 : 0);
    }

    void StartVisualizer()
    {
        if (_visualizerRunning) return;
        _visualizerRunning = true;
        _visualizer.Begin(this, true);
    }

    void StopVisualizer()
    {
        if (!_visualizerRunning) return;
        _visualizerRunning = false;
        _visualizer.Stop(this);
    }

    // ───────────────────────────── live activities ─────────────────────────────

    string _toastKind = "info";

    void ShowToast(string title, string subtitle, ImageSource? image, string glyph, string kind = "info")
    {
        // No pop-ups over a fullscreen video or game.
        if (_expanded || _hiddenForFullscreen || _fsOverlay) return;
        _toastKind = kind;
        ToastTitle.Text = title;
        ToastSub.Text = subtitle;
        if (image != null)
        {
            ToastImageBrush.ImageSource = image;
            ToastImage.Visibility = Visibility.Visible;
            ToastGlyphHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            ToastGlyph.Text = glyph;
            ToastImage.Visibility = Visibility.Collapsed;
            ToastGlyphHost.Visibility = Visibility.Visible;
        }
        _toastActive = true;
        _tuckTimer.Stop();
        // A live activity pops out of the line, then tucks back in afterwards.
        LeaveTuck();
        StopVisualizer();
        SetSpringFeel(bouncy: true);
        Retarget();
        StartAnimating();
        Fade(CollapsedLayer, 0, 80, 0, () => _toastActive || _expanded);
        Fade(ToastLayer, 1, 220, 90);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void EndToast()
    {
        if (!_toastActive) return;
        _toastActive = false;
        Fade(ToastLayer, 0, 120, 0, () => !_toastActive);
        if (!_expanded) Fade(CollapsedLayer, 1, 220, 100);
        SetSpringFeel(bouncy: false);
        UpdatePill();
        ScheduleTuck();
    }

    // ───────────────────────────── tucking (get out of the way) ─────────────────────────────

    double _lineFill;

    /// <summary>Restarts the countdown after which an untouched pill tucks into a line.</summary>
    void ScheduleTuck()
    {
        _tuckTimer.Stop();
        // In fullscreen the island gets out of the way fast, whatever the normal tuck setting is.
        double s = _fsOverlay ? 1.2 : _vm.Settings.TuckSeconds;
        if (s <= 0 || _expanded || App.Scripted)
        {
            if (s <= 0 && _tucked) Untuck();
            return;
        }
        _tuckTimer.Interval = TimeSpan.FromSeconds(s);
        _tuckTimer.Start();
    }

    void Tuck()
    {
        if (_tucked || _expanded || _toastActive) return;
        _tucked = true;
        Trace("tucked");
        _hovering = false;
        StopVisualizer();
        RestartCaptureTimer();                // a 5 px line needs no live glass: capture pauses while tucked
        HotZone.Visibility = Visibility.Visible;
        Fade(CollapsedLayer, 0, 110, 0, () => _tucked);
        Fade(ShadowBox, 0, 160);
        Fade(LineLayer, 1, 220, 120);
        Fade(Island, TuckHidden ? 0 : 1, 260, 120);
        UpdateLine();
        SetSpringFeel(bouncy: false);
        Retarget();
        StartAnimating();
    }

    /// <summary>Back to the pill (hover on the top edge, hotkey, live activity…).</summary>
    void Untuck()
    {
        if (!_tucked) return;
        Trace("untucked");
        LeaveTuck();
        UpdatePill();
        ScheduleTuck();
    }

    void LeaveTuck()
    {
        if (!_tucked) return;
        _tucked = false;
        HotZone.Visibility = Visibility.Collapsed;
        Fade(LineLayer, 0, 90, 0, () => !_tucked);
        Fade(Island, 1, 120);
        Fade(ShadowBox, 0.42, 220, 80);
        if (!_toastActive && !_expanded) Fade(CollapsedLayer, 1, 220, 90);
        SetSpringFeel(bouncy: true);
        Retarget();
        StartAnimating();
        CaptureBackdrop();
        RestartCaptureTimer();
    }

    /// <summary>The line doubles as a tiny status: focus progress in accent, or the album color while music plays.</summary>
    void UpdateLine()
    {
        if (_vm.Focus.IsActive)
        {
            LineFill.Fill = (Brush)FindResource("AccentBrush");
            LineFill.Opacity = 1;
            _lineFill = Math.Clamp(_vm.Focus.Progress, 0.04, 1);
        }
        else if (_vm.Media.IsPlaying)
        {
            LineFill.Fill = _vm.Media.ArtBrush;
            LineFill.Opacity = 0.9;
            _lineFill = 1;
        }
        else _lineFill = 0;
        LineFill.Width = LineLayer.Width * _lineFill;
    }

    bool MouseNearIsland()
    {
        if (!GetCursorPos(out var p)) return false;
        double W = _w.Value * _dpi, H = _h.Value * _dpi, pad = 24 * _dpi;
        double left = _screenRect.X + (_screenRect.Width - W) / 2, top = _screenRect.Y + _y.Value * _dpi;
        return p.X >= left - pad && p.X <= left + W + pad && p.Y >= _screenRect.Y && p.Y <= top + H + pad;
    }

    void HotZone_MouseEnter(object sender, MouseEventArgs e) => Untuck();

    void HotZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Expand();
    }

    // ───────────────────────────── glass backdrop ─────────────────────────────

    void ApplyGlassMode()
    {
        bool liquid = _vm.Settings.LiquidGlass;
        // GI_SHOWINCAPTURE=1 (debug): let real screenshots see the window, to verify on-screen composition.
        bool exclude = liquid && Environment.GetEnvironmentVariable("GI_SHOWINCAPTURE") != "1";
        if (_hwnd != IntPtr.Zero && !SetWindowDisplayAffinity(_hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
            App.Log("SetWindowDisplayAffinity failed");
        BackdropHost.Visibility = liquid ? Visibility.Visible : Visibility.Collapsed;
        if (liquid) CaptureBackdrop();
        else
        {
            _capture.Release();
            BackdropImg.Source = null;
            _tint.Target = 0.9;
        }
        RestartCaptureTimer();
        StartAnimating();
    }

    // ── background capture worker: screen reads never block the UI thread ──

    readonly AutoResetEvent _captureKick = new(false);
    readonly object _captureCfgLock = new();
    Int32Rect _capRect;
    Rect _capLuma;
    volatile bool _captureEnabled, _closing;
    volatile int _captureIntervalMs = 1000;

    void StartCaptureWorker()
    {
        new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "glass-capture" }.Start();
    }

    void CaptureLoop()
    {
        while (!_closing)
        {
            _captureKick.WaitOne(_captureEnabled ? _captureIntervalMs : Timeout.Infinite);
            if (_closing) return;
            if (!_captureEnabled || _fakeScreen != null) continue;
            Int32Rect rect; Rect luma;
            lock (_captureCfgLock) { rect = _capRect; luma = _capLuma; }
            if (rect.Width == 0) continue;
            long t0 = Stopwatch.GetTimestamp();
            bool changed;
            try { changed = _capture.Grab(rect, luma); }
            catch (Exception ex) { App.Log("capture failed: " + ex.Message); continue; }
            _bench?.AddCapture(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            if (changed) Dispatcher.BeginInvoke(OnBackdropReady, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Glass refresh rate: ~5 fps while the panel is open, 1 fps as a pill, off when tucked/hidden/solid.
    /// Blurred glass barely changes frame to frame, so a low rate is invisible but saves a lot.
    /// </summary>
    void RestartCaptureTimer()
    {
        bool on = _vm.Settings.LiquidGlass && !_hiddenForFullscreen && !_tucked;
        _captureIntervalMs = _expanded ? 200 : 1000;
        _captureEnabled = on;
        UpdateCaptureArea();
        if (on) _captureKick.Set();
    }

    /// <summary>Asks the worker for a fresh frame right away (e.g. after a state change).</summary>
    void CaptureBackdrop()
    {
        if (!_vm.Settings.LiquidGlass || _screenRect.Width == 0) return;
        if (_fakeScreen != null)
        {
            UpdateCaptureArea();
            if (_capture.GrabFrom(_fakeScreen, ScreenRegion(), _capLuma)) OnBackdropReady();
            return;
        }
        UpdateCaptureArea();
        _captureKick.Set();
    }

    /// <summary>Synchronous capture, for design snapshots only.</summary>
    void CaptureBackdropSync()
    {
        UpdateCaptureArea();
        bool changed = _fakeScreen != null ? _capture.GrabFrom(_fakeScreen, ScreenRegion(), _capLuma) : _capture.Grab(_screenRect, _capLuma);
        if (changed) OnBackdropReady();
    }

    void UpdateCaptureArea()
    {
        double W = _w.Target, H = _h.Target;
        lock (_captureCfgLock)
        {
            _capRect = _screenRect;
            _capLuma = _winW > 0 ? new Rect((_winW - W) / 2 / _winW, _y.Target / _winH, W / _winW, H / _winH) : Rect.Empty;
        }
    }

    void OnBackdropReady()
    {
        if (!_vm.Settings.LiquidGlass) return;
        ComposeGlass();
        // Brighter backgrounds get a denser tint so white text always reads.
        double target = 0.16 + 0.44 * _capture.Luminance;
        if (Math.Abs(_tint.Target - target) > 0.015)
        {
            _tint.Target = target;
            StartAnimating();
        }
    }

    /// <summary>Refracts the rim of the glass for the island's current shape (cheap; runs per frame while morphing).</summary>
    void ComposeGlass()
    {
        if (!_vm.Settings.LiquidGlass) return;
        double W = Math.Max(8, _w.Value), H = Math.Max(3, _h.Value), R = Math.Clamp(_r.Value, 0, H / 2);
        _capture.Compose(new Rect((_winW - W) / 2, _y.Value, W, H), R, new Size(_winW, _winH));
        if (!ReferenceEquals(BackdropImg.Source, _capture.Bitmap)) BackdropImg.Source = _capture.Bitmap;
    }

    // Design snapshots can render over a chosen image instead of the live screen (--backdrop <image>).
    BitmapSource? _fakeScreen;

    Int32Rect ScreenRegion()
    {
        var mon = PrimaryMonitorRect();
        return new Int32Rect(_screenRect.X - mon.Left, _screenRect.Y - mon.Top, _screenRect.Width, _screenRect.Height);
    }

    void LoadFakeScreen(string path)
    {
        var mon = PrimaryMonitorRect();
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        // cover-fit the image to the monitor, like a wallpaper
        double scale = Math.Max((double)mon.Width / img.PixelWidth, (double)mon.Height / img.PixelHeight);
        var scaled = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        int ox = (scaled.PixelWidth - mon.Width) / 2, oy = (scaled.PixelHeight - mon.Height) / 2;
        var cropped = new CroppedBitmap(scaled, new Int32Rect(ox, oy, mon.Width, mon.Height));
        var final = new WriteableBitmap(new FormatConvertedBitmap(cropped, PixelFormats.Bgr32, null, 0));
        final.Freeze();
        _fakeScreen = final;
    }

    // ───────────────────────────── fullscreen / memory ─────────────────────────────

    void CheckFullscreen()
    {
        // Nobody is looking: stop the music bars (the only continuous animation) until the user is back.
        bool away = IdleSeconds() > 90;
        if (away != _userAway)
        {
            _userAway = away;
            UpdatePill();
        }
        if (App.Scripted) return;
        var kind = _vm.Settings.HideInFullscreen ? GetFullscreen() : Fullscreen.None;

        // F11 / video fullscreen: invisible, but the top-centre notch area still reveals it.
        bool overlay = kind == Fullscreen.Overlay;
        if (overlay != _fsOverlay)
        {
            _fsOverlay = overlay;
            Trace((overlay ? "fullscreen: hidden until hovered" : "fullscreen ended") + $" (foreground: {ForegroundDescription()})");
            if (overlay) EnterFullscreenTuck(); else LeaveFullscreenTuck();
        }

        // Exclusive fullscreen (games, presentations): nothing can draw on top, so hide outright.
        bool exclusive = kind == Fullscreen.Exclusive;
        if (exclusive == _hiddenForFullscreen || _expanded) return;
        Trace(exclusive ? "hidden: exclusive fullscreen" : "shown: exclusive fullscreen ended");
        if (exclusive)
        {
            _hiddenForFullscreen = true;
            RestartCaptureTimer();
            StopVisualizer();
            Visibility = Visibility.Hidden;
        }
        else ShowFromFullscreen();
    }

    bool _fsOverlay;

    static string ForegroundDescription()
    {
        var fg = GetForegroundWindow();
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        return fg == IntPtr.Zero ? "none" : sb.ToString();
    }

    /// <summary>When tucked, show nothing (fullscreen, or the user's choice) rather than the slim line.</summary>
    bool TuckHidden => _fsOverlay || _vm.Settings.TuckStyle == "Hidden";

    void EnterFullscreenTuck()
    {
        // Stay above the fullscreen window so the hover zone keeps working.
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (_expanded) return;                      // closing the panel tucks it
        if (_toastActive) { _toastTimer.Stop(); EndToast(); }
        if (_tucked) Fade(Island, 0, 200);
        else Tuck();
    }

    void LeaveFullscreenTuck()
    {
        if (_tucked) Fade(Island, TuckHidden ? 0 : 1, 220);
        ScheduleTuck();
    }

    void ShowFromFullscreen()
    {
        _hiddenForFullscreen = false;
        Visibility = Visibility.Visible;
        PlaceWindow();
        CaptureBackdrop();
        RestartCaptureTimer();
        UpdatePill();
    }

    void TrimMemory()
    {
        if (_expanded) return;
        ThumbLoader.ReleaseAll();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        using var p = Process.GetCurrentProcess();
        EmptyWorkingSet(p.Handle);
        Trace($"trim: managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB, private {p.PrivateMemorySize64 / 1048576.0:F1} MB");
    }

    // ───────────────────────────── input ─────────────────────────────

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _tuckTimer.Stop();
        if (_tucked) { Untuck(); return; }
        if (_expanded || _toastActive) return;
        _hovering = true;
        SetSpringFeel(bouncy: true);
        Retarget();
        StartAnimating();
        UpdatePillLabels();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_expanded)
        {
            if (!_keyboardMode) _collapseTimer.Start();
            return;
        }
        _hovering = false;
        Retarget();
        StartAnimating();
        UpdatePillLabels();
        ScheduleTuck();
    }

    void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        if (_toastActive && _toastKind == "music")
        {
            _toastTimer.Stop();
            EndToast();
            _vm.Media.OpenPlayer();
            return;
        }
        Expand(_toastActive && _toastKind == "wallpaper" ? "Wallpapers" : null);
    }

    /// <summary>Album art (card, pill or song toast): bring the player that is playing to the front.</summary>
    void OpenPlayer_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm.Media.OpenPlayer()) Collapse();
    }

    /// <summary>Like the iPhone's island: tapping the album art opens the app, tapping elsewhere expands.</summary>
    void PillArt_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded || _tucked) return;
        e.Handled = true;
        _vm.Media.OpenPlayer();
    }

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        var tab = sender == TabWalls ? "Wallpapers" : sender == TabTheme ? "Theme" : sender == TabFocus ? "Focus" : "Home";
        if (tab != _tab) SelectTab(tab);
    }

    void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    void Quit_Click(object sender, RoutedEventArgs e) => Close();

    void TodoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.Focus.AddTodoCommand.Execute(null);
        e.Handled = true;
    }

    void EnterKeyboard()
    {
        if (_keyboardMode) return;
        _prevForeground = GetForegroundWindow();
        _keyboardMode = true;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        Activate();
    }

    void ExitKeyboard(bool restoreFocus)
    {
        if (!_keyboardMode) return;
        _keyboardMode = false;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        Keyboard.ClearFocus();
        if (restoreFocus && _prevForeground != IntPtr.Zero && IsWindow(_prevForeground) && GetForegroundWindow() == _hwnd)
            SetForegroundWindow(_prevForeground);
    }

    void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = ScrollBy(-e.Delta * 0.85);
    }

    bool ScrollBy(double delta)
    {
        _wallScroll ??= WallList.Template?.FindName("WallScroll", WallList) as ScrollViewer;
        if (_wallScroll == null) return false;
        if (!_scrolling) _scrollTarget = _wallScroll.VerticalOffset;
        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, _wallScroll.ScrollableHeight);
        if (_scrolling) return true;
        _scrolling = true;
        CompositionTarget.Rendering += ScrollFrame;
        return true;
    }

    TimeSpan _lastScrollFrame;
    double _lastScrollOffset = double.NaN;
    int _stuckFrames;

    void ScrollFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastScrollFrame) return;
        double dt = _lastScrollFrame == default ? 1 / 60.0 : Math.Min((t - _lastScrollFrame).TotalSeconds, 1 / 20.0);
        _lastScrollFrame = t;

        var sv = _wallScroll!;
        double cur = sv.VerticalOffset;
        // Stop when hidden, when the list can no longer move (e.g. the tab changed mid-glide), or on arrival.
        // Without this guard the loop could run forever and keep the whole app rendering every frame.
        _stuckFrames = !double.IsNaN(_lastScrollOffset) && Math.Abs(cur - _lastScrollOffset) < 0.01 ? _stuckFrames + 1 : 0;
        bool stuck = _stuckFrames >= 2;
        if (!sv.IsVisible || stuck || Math.Abs(_scrollTarget - cur) < 0.5)
        {
            if (sv.IsVisible && !stuck) sv.ScrollToVerticalOffset(_scrollTarget);
            StopScroll();
            return;
        }
        _lastScrollOffset = cur;
        // frame-rate independent glide (same feel at 60 Hz and 144 Hz)
        sv.ScrollToVerticalOffset(cur + (_scrollTarget - cur) * (1 - Math.Exp(-dt * 16)));
    }

    void StopScroll()
    {
        if (!_scrolling) return;
        CompositionTarget.Rendering -= ScrollFrame;
        _scrolling = false;
        _lastScrollFrame = default;
        _lastScrollOffset = double.NaN;
        _stuckFrames = 0;
    }

    void MonitorChip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && !_monitorChips.Contains(b)) _monitorChips.Add(b);
        UpdateChips();
    }

    void PresetChip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && !_presetChips.Contains(b)) _presetChips.Add(b);
        UpdateChips();
    }

    void UpdateChips()
    {
        foreach (var b in _monitorChips)
            b.Tag = (b.CommandParameter as string == "All" ? 0 : int.TryParse(b.CommandParameter as string, out var i) ? i : -1) == _vm.Walls.Monitor;
        foreach (var b in _presetChips)
            b.Tag = b.CommandParameter is int m && m == _vm.Focus.Minutes;
    }

    void UpdateWallEmpty() =>
        WallEmpty.Visibility = _vm.Walls.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateTodoEmpty() =>
        TodoEmpty.Visibility = _vm.Focus.Todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ───────────────────────────── helpers ─────────────────────────────

    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Opacity fade; optionally collapses the element afterwards if <paramref name="collapseIf"/> still holds.</summary>
    void Fade(UIElement el, double to, int ms, int delay = 0, Func<bool>? collapseIf = null)
    {
        if (to > 0) el.Visibility = Visibility.Visible;
        if (_instant)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = to;
            if (to <= 0 && collapseIf?.Invoke() == true) el.Visibility = Visibility.Collapsed;
            return;
        }
        var a = new DoubleAnimation(to, Ms(ms)) { BeginTime = Ms(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (collapseIf != null && to <= 0)
            a.Completed += (_, _) => { if (collapseIf()) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(OpacityProperty, a);
    }

    // ───────────────────────────── design snapshots (--snapshot <dir> [--demo]) ─────────────────────────────

    async Task RunSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        _instant = true;
        if (App.BackdropPath != null)
        {
            LoadFakeScreen(App.BackdropPath);
            CaptureBackdrop();
        }
        if (App.Demo)
        {
            _vm.Media.LoadDemo();
            if (_vm.Focus.Todos.Count == 0)
                foreach (var t in new[] { "Ship the island v1", "Review wallpaper picks", "Reply to design feedback" })
                    _vm.Focus.Todos.Add(new TodoItem(t, t.StartsWith("Review"), () => { }));
        }
        await Settle();
        await Shot(dir, "01-pill");

        _hovering = true; Retarget(); UpdatePillLabels();
        await Settle();
        await Shot(dir, "02-pill-hover");
        _hovering = false;

        Expand("Home");
        await Settle();
        await Shot(dir, "03-home");

        SelectTab("Wallpapers");
        await _vm.Walls.EnsureLoadedAsync();
        await Task.Delay(1800);
        await Settle();
        await Shot(dir, "04-wallpapers");

        SelectTab("Theme");
        await Settle();
        await Shot(dir, "05-theme");

        SelectTab("Focus");
        await Settle();
        await Shot(dir, "06-focus");

        Collapse();
        if (App.Demo) _vm.Focus.Demo(TimeSpan.FromSeconds(17 * 60 + 42));
        await Settle();
        await Shot(dir, "07-pill-timer");

        _vm.Focus.ResetCommand.Execute(null);
        ShowToast("Space Tacos!", "Scene wallpaper applied", null, "");
        await Settle();
        await Shot(dir, "08-toast");

        EndToast();
        Tuck();
        await Settle();
        await Shot(dir, "09-tucked-line");

        Close();
    }

    // ───────────────────────────── benchmark (--bench <file>) ─────────────────────────────

    FrameStats? _bench;

    async Task RunBench(string file)
    {
        await _vm.Walls.EnsureLoadedAsync();
        WarmUpPanels();             // same as a real startup
        await Task.Delay(500);
        var s = _bench = new FrameStats();
        s.Attach();
        async Task Phase(string name, int ms, Action? act = null)
        {
            s.Begin(name);
            act?.Invoke();
            await Task.Delay(ms);
            s.End();
        }

        await Phase("pill-idle", 1500);
        await Phase("hover-in", 800, () => Island_MouseEnter(Island, null!));
        await Phase("hover-out", 800, () => Island_MouseLeave(Island, null!));
        await Phase("expand", 1200, () => Expand("Home"));
        await Phase("tab-walls", 1500, () => SelectTab("Wallpapers"));
        s.Begin("scroll");
        for (int i = 0; i < 24; i++) { ScrollBy(i < 12 ? 140 : -140); await Task.Delay(125); }
        await Task.Delay(300);
        s.End();
        using var prof = new AllocProfiler { Recording = true };
        await Phase("tab-theme", 800, () => SelectTab("Theme"));
        await Phase("tab-focus", 800, () => SelectTab("Focus"));
        await Phase("tab-home", 800, () => SelectTab("Home"));
        await Phase("open-idle", 2000);
        await Phase("collapse", 1200, Collapse);
        await Phase("tuck", 1000, Tuck);
        await Phase("untuck", 1000, Untuck);
        s.Detach();
        prof.Recording = false;
        // Real-world cost without the benchmark's forced redraws: the pill at rest (music bars if music plays).
        await Task.Delay(1500);
        await Phase("pill-real", 5000);
        File.WriteAllText(file + ".alloc.txt", "Top allocating types after the scroll phase:\n" + prof.Top(15));

        using (var p = Process.GetCurrentProcess())
            File.WriteAllText(file, s + $"working set {p.WorkingSet64 / 1048576.0:F1} MB, private {p.PrivateMemorySize64 / 1048576.0:F1} MB, managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB, gen2 GCs {GC.CollectionCount(2)}\n");
        Close();
    }

    async Task Settle()
    {
        Trace($"settle: tab={_tab} expanded={_expanded} animating={_animating}");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        SnapAll();
        await Task.Delay(150);
        SnapAll();
    }

    async Task Shot(string dir, string name)
    {
        CaptureBackdropSync();
        ComposeGlass();
        SnapshotBg.Source = _fakeScreen != null ? new CroppedBitmap(_fakeScreen, ScreenRegion()) : BackdropCapture.CaptureRaw(_screenRect);
        SnapshotBg.Visibility = Visibility.Visible;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var rtb = new RenderTargetBitmap(_screenRect.Width, _screenRect.Height, 96 * _dpi, 96 * _dpi, PixelFormats.Pbgra32);
        rtb.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(fs);
        SnapshotBg.Visibility = Visibility.Collapsed;
    }
}
