using System.Runtime.InteropServices;

namespace GlassIsland.Services;

/// <summary>Master volume of the default playback device via Core Audio (no extra packages).</summary>
public static class AudioService
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var dev) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) == 0 ? o as IAudioEndpointVolume : null;
        }
        catch { return null; }
    }

    public static (double Volume, bool Muted) Get()
    {
        var ep = Endpoint();
        if (ep == null) return (0, false);
        ep.GetMasterVolumeLevelScalar(out var v);
        ep.GetMute(out var m);
        return (Math.Round(v * 100), m);
    }

    public static void SetVolume(double percent)
    {
        var g = Guid.Empty;
        Endpoint()?.SetMasterVolumeLevelScalar((float)Math.Clamp(percent / 100.0, 0, 1), ref g);
    }

    public static void SetMute(bool mute)
    {
        var g = Guid.Empty;
        Endpoint()?.SetMute(mute, ref g);
    }
}
