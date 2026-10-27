using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassIsland.Core;

/// <summary>
/// Stores the Gemini API key encrypted with Windows DPAPI (CurrentUser scope): only this Windows account on
/// this PC can decrypt it. Kept in %APPDATA%\GlassIsland, never in settings.json or the project folder.
/// </summary>
public static class SecretStore
{
    static readonly string FilePath = Path.Combine(AppSettings.DataDir, "gemini.key");
    static readonly byte[] Entropy = "GlassIsland.Gemini.v1"u8.ToArray();

    public static bool HasKey => File.Exists(FilePath);

    public static void Save(string key)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        File.WriteAllBytes(FilePath, Protect(Encoding.UTF8.GetBytes(key.Trim())));
    }

    public static string? Load()
    {
        try { return File.Exists(FilePath) ? Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(FilePath))) : null; }
        catch { return null; }
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB data, string? description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB data, IntPtr description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    static byte[] Protect(byte[] plain) => Run(plain, protect: true);
    static byte[] Unprotect(byte[] cipher) => Run(cipher, protect: false);

    static byte[] Run(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var data = new DATA_BLOB { cbData = input.Length, pbData = inH.AddrOfPinnedObject() };
            var ent = new DATA_BLOB { cbData = Entropy.Length, pbData = enH.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref data, "Glass Island", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { inH.Free(); enH.Free(); }
    }
}
