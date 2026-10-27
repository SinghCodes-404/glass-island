using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using Microsoft.Win32;

namespace GlassIsland.Core;

/// <summary>
/// GlassIsland.exe is a single self-installing program. Run from anywhere (e.g. Downloads), it installs or updates
/// itself for the current user — no admin: %LOCALAPPDATA%\Programs\GlassIsland, a Start menu entry, a sign-in task,
/// and an entry in Settings → Apps whose Uninstall button runs "GlassIsland.exe --uninstall".
/// </summary>
public static class Installer
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassIsland");
    public static string Exe => Path.Combine(Dir, "GlassIsland.exe");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glass Island.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassIsland";
    const string Title = "Glass Island";

    /// <summary>Single-file builds (the distributable program) have no assembly path; dev builds do.</summary>
#pragma warning disable IL3000 // an empty Location is exactly the signal we want
    static bool IsPackaged => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>The path sign-in should launch: the installed program, or this build when developing.</summary>
    public static string LaunchPath => RunningInstalled || File.Exists(Exe) && IsPackaged ? Exe : Environment.ProcessPath!;

    /// <summary>Handles install / update / uninstall. Returns true when this process should exit.</summary>
    public static bool HandleStartup(string[] args)
    {
        if (args.Contains("--uninstall")) { Uninstall(); return true; }
        if (RunningInstalled) { EnsureRegistered(); return false; }
        if (!IsPackaged) return false;   // dev build: just run

        bool update = File.Exists(Exe);
        bool quiet = args.Contains("--install");
        if (!quiet)
        {
            var msg = update
                ? "Update Glass Island to this version?\n\nYour settings, tasks and API key are kept."
                : "Install Glass Island?\n\nIt installs for your account only (no admin needed), adds a Start menu entry, " +
                  "and starts automatically when you sign in. You can remove it any time from Settings → Apps.";
            if (MessageBox.Show(msg, Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return true;
        }

        try
        {
            bool autostart = !update || StartupTask.IsEnabled || StartupTask.HasLegacyRunEntry();
            StopOtherInstances();
            Directory.CreateDirectory(Dir);
            // Older versions installed many files; the program is one exe now, so clear everything else in our folder.
            foreach (var f in Directory.EnumerateFileSystemEntries(Dir))
                if (!string.Equals(Path.GetFileName(f), "GlassIsland.exe", StringComparison.OrdinalIgnoreCase))
                    try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch { }
            File.Copy(Environment.ProcessPath!, Exe, overwrite: true);
            CreateShortcut();
            WriteUninstallEntry();
            if (autostart) StartupTask.Enable(Exe);
            Process.Start(new ProcessStartInfo(Exe, update ? "--updated" : "--installed") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            App.Log("Install failed: " + ex);
            MessageBox.Show("Glass Island couldn't be installed:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    /// <summary>Keeps the Start menu entry and Settings → Apps entry present, and moves old Run-list autostart to the task.</summary>
    static void EnsureRegistered()
    {
        try
        {
            if (!File.Exists(Shortcut)) CreateShortcut();
            WriteUninstallEntry();
            if (StartupTask.HasLegacyRunEntry()) StartupTask.Enable(Exe);
        }
        catch (Exception ex) { App.Log("Registration check failed: " + ex.Message); }
    }

    static void Uninstall()
    {
        if (MessageBox.Show("Remove Glass Island from this PC?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        bool wipeData = MessageBox.Show("Also delete your Glass Island settings, tasks, thumbnail cache and stored Gemini API key?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        StopOtherInstances();
        StartupTask.Disable();
        try { File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (wipeData)
        {
            TryDeleteDir(AppSettings.DataDir);
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassIsland"));
        }
        // This exe may be the installed one, which can't delete itself while running: remove the folder just after exit.
        if (Directory.Exists(Dir))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Dir}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
        MessageBox.Show("Glass Island was removed.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static void StopOtherInstances()
    {
        foreach (var p in Process.GetProcessesByName("GlassIsland"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
            p.Dispose();
        }
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    static void WriteUninstallEntry()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey, true);
        var version = typeof(Installer).Assembly.GetName().Version;
        k.SetValue("DisplayName", Title);
        k.SetValue("DisplayVersion", version == null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}");
        k.SetValue("Publisher", "Glass Island");
        k.SetValue("DisplayIcon", $"\"{Exe}\",0");
        k.SetValue("InstallLocation", Dir);
        k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord); } catch { }
    }

    // ── Start menu shortcut via IShellLink ──

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath(IntPtr file, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription(IntPtr name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation(IntPtr path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    static void CreateShortcut()
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(Exe);
        link.SetWorkingDirectory(Dir);
        link.SetDescription(Title);
        link.SetIconLocation(Exe, 0);
        ((IPersistFile)link).Save(Shortcut, true);
        Marshal.ReleaseComObject(link);
    }
}
