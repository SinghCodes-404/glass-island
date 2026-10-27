using System.IO;
using System.Windows;
using GlassIsland.Core;

namespace GlassIsland;

public partial class App : Application
{
    Mutex? _single;

    public static string? SnapshotDir { get; private set; }
    public static bool Demo { get; private set; }
    public static string? BackdropPath { get; private set; }
    public static string? BenchFile { get; private set; }
    /// <summary>"installed" / "updated" right after the self-installer ran; shown as a welcome toast.</summary>
    public static string? JustInstalled { get; private set; }
    /// <summary>--ask "prompt" (repeatable): send prompts to the assistant, log the replies, exit.</summary>
    public static List<string> AskPrompts { get; } = [];

    /// <summary>Scripted runs (snapshots, benchmark, --ask): no hotkey, no single-instance lock, no auto-tuck.</summary>
    public static bool Scripted => SnapshotDir != null || BenchFile != null || AskPrompts.Count > 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Avoid blocking full collections during interaction; memory is handed back explicitly when idle.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length) SnapshotDir = e.Args[++i];
            else if (e.Args[i] == "--demo") Demo = true;
            else if (e.Args[i] == "--backdrop" && i + 1 < e.Args.Length) BackdropPath = e.Args[++i];
            else if (e.Args[i] == "--bench" && i + 1 < e.Args.Length) BenchFile = e.Args[++i];
            else if (e.Args[i] == "--ask" && i + 1 < e.Args.Length) AskPrompts.Add(e.Args[++i]);
        }

        if (!Scripted)
        {
            // GlassIsland.exe is self-installing: install / update / uninstall happen here, before anything else.
            if (Installer.HandleStartup(e.Args)) { Shutdown(); return; }
            JustInstalled = e.Args.Contains("--installed") ? "installed" : e.Args.Contains("--updated") ? "updated" : null;

            _single = new Mutex(true, "GlassIsland.SingleInstance", out bool first);
            if (!first) { Log("Already running; this second launch exits."); Shutdown(); return; }
            Log($"Starting from {Environment.ProcessPath}");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { Log("Task: " + args.Exception); args.SetObserved(); };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var path = Path.Combine(AppSettings.DataDir, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}
