using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassIsland.Core;

/// <summary>
/// Maps a media session's AppUserModelID (what Windows media controls report: "Spotify.exe", Firefox's
/// install hash, a packaged app id…) to a real top-level window, so "open the player" can bring it forward.
/// </summary>
public static class AppWindows
{
    sealed record Candidate(IntPtr Hwnd, string Title, string Exe, string? Aumid);

    /// <summary>Brings the app's window to the front. For browsers, prefers the window whose title has the song.</summary>
    public static bool Activate(string aumid, string? titleHint)
    {
        var c = Find(aumid, titleHint);
        if (c == null) return aumid.Contains('!') && ActivatePackaged(aumid);
        var h = c.Hwnd;
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        if (!SetForegroundWindow(h))
        {
            // Foreground lock: a synthetic Alt tap lets this process hand focus over (standard workaround).
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }
        return true;
    }

    /// <summary>Diagnostics: which window <see cref="Activate"/> would pick.</summary>
    public static string Describe(string aumid, string? titleHint) =>
        Find(aumid, titleHint) is { } c ? $"'{c.Title}' ({Path.GetFileName(c.Exe)}, aumid={c.Aumid ?? "-"})" : "no window";

    /// <summary>"Firefox", "Spotify"… for the card. Falls back to a tidy version of the id.</summary>
    public static string DisplayName(string aumid)
    {
        var c = Find(aumid, null);
        if (c != null)
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(c.Exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(c.Exe);
        }
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return aumid[..^4];
        if (aumid.Contains('!'))
        {
            var family = aumid.Split('!')[0].Split('_')[0];   // "SpotifyAB.SpotifyMusic"
            return family.Split('.')[^1].Replace("Music", "").Trim() is { Length: > 0 } n ? n : family;
        }
        return "the player";
    }

    static readonly string[] MusicSites = ["YouTube Music", "YouTube", "Spotify", "SoundCloud", "Apple Music", "Deezer", "JioSaavn", "Gaana", "Amazon Music", "Tidal"];

    static Candidate? Find(string aumid, string? titleHint)
    {
        string exeHint = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? aumid[..^4] : aumid;
        string packageHint = aumid.Contains('!') ? aumid.Split('!')[0].Split('_')[0] : "";
        Candidate? best = null;
        int bestScore = 0;
        foreach (var c in TopLevelWindows())
        {
            int score = 0;
            if (c.Aumid != null && string.Equals(c.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) score = 10;
            else if (string.Equals(Path.GetFileNameWithoutExtension(c.Exe), exeHint, StringComparison.OrdinalIgnoreCase)) score = 8;
            else if (packageHint.Length > 0 && c.Exe.Contains(packageHint, StringComparison.OrdinalIgnoreCase)) score = 6;
            if (score == 0) continue;
            if (!string.IsNullOrWhiteSpace(titleHint) && c.Title.Contains(titleHint, StringComparison.CurrentCultureIgnoreCase)) score += 3;
            else if (MusicSites.Any(s => c.Title.Contains(s, StringComparison.OrdinalIgnoreCase))) score += 2;   // browser window showing a music tab
            if (score > bestScore) { best = c; bestScore = score; }   // EnumWindows is top-most first: ties keep the frontmost
        }
        return best;
    }

    static List<Candidate> TopLevelWindows()
    {
        var list = new List<Candidate>();
        int self = Environment.ProcessId;
        var exeCache = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            if (!exeCache.TryGetValue(pid, out var exe)) exeCache[pid] = exe = ProcessPath(pid);
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            list.Add(new Candidate(h, sb.ToString(), exe, WindowAumid(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static string ProcessPath(uint pid)
    {
        IntPtr hp = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (hp == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(hp); }
    }

    // ── a window's explicit AppUserModelID, via its shell property store ──

    static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    static string? WindowAumid(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    // ── packaged apps (Store Spotify, Media Player…) with no open window: activate by AUMID ──

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    static bool ActivatePackaged(string aumid)
    {
        try { return ((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(aumid, null, 0, out _) == 0; }
        catch { return false; }
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
}
