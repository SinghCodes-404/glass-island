using System.Windows.Media;
using GlassIsland.Core;
using Microsoft.Win32;

namespace GlassIsland.Services;

/// <summary>Reads and writes the per-user Windows personalization settings (all HKCU, no admin needed).</summary>
public sealed class ThemeService
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string Dwm = @"Software\Microsoft\Windows\DWM";

    static int GetInt(string key, string name, int fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : fallback;
    }

    static void SetInt(string key, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public bool IsDark => GetInt(Personalize, "AppsUseLightTheme", 1) == 0;

    public void SetDark(bool dark)
    {
        SetInt(Personalize, "AppsUseLightTheme", dark ? 0 : 1);
        SetInt(Personalize, "SystemUsesLightTheme", dark ? 0 : 1);
        Native.BroadcastSettingChange("ImmersiveColorSet");
    }

    public bool Transparency
    {
        get => GetInt(Personalize, "EnableTransparency", 1) == 1;
        set { SetInt(Personalize, "EnableTransparency", value ? 1 : 0); Native.BroadcastSettingChange("ImmersiveColorSet"); }
    }

    public bool AccentOnTaskbar
    {
        get => GetInt(Personalize, "ColorPrevalence", 0) == 1;
        set { SetInt(Personalize, "ColorPrevalence", value ? 1 : 0); Native.BroadcastSettingChange("ImmersiveColorSet"); }
    }

    public Color GetAccent()
    {
        unchecked
        {
            int abgr = GetInt(Dwm, "AccentColor", (int)0xFFD77800);
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
    }

    public void SetAccent(Color c)
    {
        // Windows keeps an 8-step palette around the accent (index 3 is the accent itself).
        Color[] pal =
        [
            ColorTools.Lighten(c, 0.62), ColorTools.Lighten(c, 0.42), ColorTools.Lighten(c, 0.22), c,
            ColorTools.Darken(c, 0.2), ColorTools.Darken(c, 0.38), ColorTools.Darken(c, 0.55), ColorTools.Darken(c, 0.7),
        ];
        var bytes = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            bytes[i * 4] = pal[i].R; bytes[i * 4 + 1] = pal[i].G; bytes[i * 4 + 2] = pal[i].B; bytes[i * 4 + 3] = 0xFF;
        }
        static int Abgr(Color x) => unchecked((int)(0xFF000000u | ((uint)x.B << 16) | ((uint)x.G << 8) | x.R));
        static int Argb(Color x, byte a) => unchecked((int)(((uint)a << 24) | ((uint)x.R << 16) | ((uint)x.G << 8) | x.B));

        using (var k = Registry.CurrentUser.CreateSubKey(Accent, true))
        {
            k.SetValue("AccentPalette", bytes, RegistryValueKind.Binary);
            k.SetValue("AccentColorMenu", Abgr(pal[3]), RegistryValueKind.DWord);
            k.SetValue("StartColorMenu", Abgr(pal[4]), RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(Dwm, true))
        {
            k.SetValue("AccentColor", Abgr(c), RegistryValueKind.DWord);
            k.SetValue("ColorizationColor", Argb(c, 0xC4), RegistryValueKind.DWord);
            k.SetValue("ColorizationAfterglow", Argb(c, 0xC4), RegistryValueKind.DWord);
        }
        Native.BroadcastSettingChange("ImmersiveColorSet");
    }

    /// <summary>Sign-in autostart, via a per-user Task Scheduler task (see <see cref="StartupTask"/>).</summary>
    public bool StartWithWindows
    {
        get => StartupTask.IsEnabled || StartupTask.HasLegacyRunEntry();
        set
        {
            if (value) StartupTask.Enable(Installer.LaunchPath);
            else StartupTask.Disable();
        }
    }
}
