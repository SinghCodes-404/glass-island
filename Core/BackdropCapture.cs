using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static GlassIsland.Core.Native;

namespace GlassIsland.Core;

/// <summary>
/// Liquid glass, computed on a tiny bitmap (≈190×114 px for the whole window):
///   1. <see cref="Grab"/> — on a background thread — copies the screen behind the window at 1/4 resolution
///      (the island window itself is excluded from capture), blurs it and boosts saturation;
///   2. <see cref="Compose"/> — on the UI thread, well under a millisecond — refracts the rim for the
///      island's current shape (near the edge the glass samples the backdrop from further out, with a touch
///      of chromatic dispersion) and uploads the result.
/// Screen reads can take several milliseconds, so they never run on the UI thread: animation frames are
/// never held up by a capture.
/// </summary>
public sealed class BackdropCapture : IDisposable
{
    // ── grab side (any thread, serialized by _grabLock) ──
    readonly object _grabLock = new();
    IntPtr _memDc, _dib, _old, _bits;
    int _gw, _gh;
    int[] _tmp = [], _work = [];
    long _lastHash;

    // ── published result (swapped under _gate) ──
    readonly object _gate = new();
    int[] _blur = [];
    int _w, _h;
    int _version;

    // ── compose side (UI thread) ──
    int[] _out = [];
    int _composedVersion = -1;
    Rect _lastShape;
    double _lastRadius = -1;

    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Average perceived luminance (0..1) of the backdrop under the island.</summary>
    public double Luminance { get; private set; } = 0.3;

    public int Divisor { get; set; } = 4;
    public int BlurRadius { get; set; } = 2;
    public double Saturation { get; set; } = 1.6;

    /// <summary>Grabs <paramref name="screen"/> (physical px) and processes it. Thread-safe. True when it changed.</summary>
    public bool Grab(Int32Rect screen, Rect lumaArea)
    {
        lock (_grabLock)
        {
            int w = Math.Max(4, screen.Width / Divisor), h = Math.Max(4, screen.Height / Divisor);
            EnsureGrab(w, h);
            IntPtr sdc = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(_memDc, HALFTONE);
                SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                StretchBlt(_memDc, 0, 0, w, h, sdc, screen.X, screen.Y, screen.Width, screen.Height, SRCCOPY);
            }
            finally { ReleaseDC(IntPtr.Zero, sdc); }
            Marshal.Copy(_bits, _tmp, 0, w * h);
            return Process(w, h, lumaArea);
        }
    }

    /// <summary>Same as <see cref="Grab"/> but from an image (design snapshots over a chosen backdrop).</summary>
    public bool GrabFrom(BitmapSource screenImage, Int32Rect region, Rect lumaArea)
    {
        lock (_grabLock)
        {
            int w = Math.Max(4, region.Width / Divisor), h = Math.Max(4, region.Height / Divisor);
            EnsureGrab(w, h);
            var crop = new CroppedBitmap(screenImage, region);
            var small = new TransformedBitmap(crop, new ScaleTransform((double)w / region.Width, (double)h / region.Height));
            var bgr = new FormatConvertedBitmap(small, PixelFormats.Bgr32, null, 0);
            var buf = new int[bgr.PixelWidth * bgr.PixelHeight];
            bgr.CopyPixels(buf, bgr.PixelWidth * 4, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    _tmp[y * w + x] = buf[Math.Min(y, bgr.PixelHeight - 1) * bgr.PixelWidth + Math.Min(x, bgr.PixelWidth - 1)];
            _lastHash = 0;
            return Process(w, h, lumaArea);
        }
    }

    bool Process(int w, int h, Rect lumaArea)
    {
        int n = w * h;
        // Cheap change detection: skip all work (and the WPF re-render) when nothing moved behind us.
        long hash = 17;
        for (int i = 0; i < n; i += 3) hash = hash * 31 + _tmp[i];
        if (hash == _lastHash) return false;
        _lastHash = hash;

        Array.Copy(_tmp, _work, n);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(_work, _tmp, w, h, BlurRadius);
            BoxV(_tmp, _work, w, h, BlurRadius);
        }
        Grade(_work, n, Saturation);
        Luminance = MeasureLuma(_work, w, h, lumaArea);

        lock (_gate)
        {
            (_blur, _work) = (_work, _blur.Length == n ? _blur : new int[n]);
            _w = w; _h = h;
            _version++;
        }
        return true;
    }

    /// <summary>
    /// UI thread. Writes the refracted glass for an island at <paramref name="shape"/> with corner
    /// <paramref name="radius"/> (window DIPs) into <see cref="Bitmap"/>; <paramref name="window"/> is the window size.
    /// </summary>
    public void Compose(Rect shape, double radius, Size window)
    {
        lock (_gate)
        {
            if (_w == 0) return;
            if (_version == _composedVersion && shape == _lastShape && Math.Abs(radius - _lastRadius) < 0.01) return;
            _composedVersion = _version;
            _lastShape = shape;
            _lastRadius = radius;

            if (Bitmap == null || Bitmap.PixelWidth != _w || Bitmap.PixelHeight != _h)
            {
                Bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);
                _out = new int[_w * _h];
            }
            Array.Copy(_blur, _out, _w * _h);
            Refract(shape, radius, window);
            Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), _out, _w * 4, 0);
        }
    }

    void Refract(Rect shape, double radius, Size window)
    {
        double sx = _w / window.Width, sy = _h / window.Height;
        double cx = (shape.X + shape.Width / 2) * sx, cy = (shape.Y + shape.Height / 2) * sy;
        double s = (sx + sy) / 2;
        double hx = shape.Width / 2 * s, hy = shape.Height / 2 * s, r = Math.Min(radius * s, Math.Min(hx, hy));
        double band = Math.Min(16 * s, Math.Min(hx, hy) * 0.75);   // width of the refracting rim
        double strength = band * 1.15;                               // how far the rim reaches outward
        if (band < 0.5) return;

        int x0 = Math.Max(0, (int)(cx - hx)), x1 = Math.Min(_w - 1, (int)Math.Ceiling(cx + hx));
        int y0 = Math.Max(0, (int)(cy - hy)), y1 = Math.Min(_h - 1, (int)Math.Ceiling(cy + hy));
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5 - cy;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5 - cx;
                // signed distance to a rounded rectangle (negative inside)
                double qx = Math.Abs(px) - (hx - r), qy = Math.Abs(py) - (hy - r);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                double d = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
                double inside = -d;
                if (inside <= 0 || inside >= band) continue;

                // outward normal of the shape at this point
                double nx, ny;
                if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); nx = qx / l; ny = qy / l; }
                else if (qx > qy) { nx = 1; ny = 0; }
                else { nx = 0; ny = 1; }
                nx *= Math.Sign(px); ny *= Math.Sign(py);

                double t = 1 - inside / band;      // 0 at the inner edge of the rim, 1 at the very edge
                double k = t * t * strength;        // lens falloff
                double bx = x + 0.5 + nx * k, by = y + 0.5 + ny * k;
                // slight dispersion: red bends a little more than blue
                int red = (Sample(bx + nx * k * 0.12, by + ny * k * 0.12) >> 16) & 255;
                int grn = (Sample(bx, by) >> 8) & 255;
                int blu = Sample(bx - nx * k * 0.12, by - ny * k * 0.12) & 255;
                // the rim also catches a bit of light
                double lift = 1 + 0.18 * t * t;
                _out[y * _w + x] = (Clamp(red * lift) << 16) | (Clamp(grn * lift) << 8) | Clamp(blu * lift);
            }
        }
    }

    int Sample(double x, double y)
    {
        x = Math.Clamp(x - 0.5, 0, _w - 1.001);
        y = Math.Clamp(y - 0.5, 0, _h - 1.001);
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        int i = iy * _w + ix;
        int a = _blur[i], b = _blur[i + 1], c = _blur[i + _w], d = _blur[i + _w + 1];
        int Mix(int sh)
        {
            double top = ((a >> sh) & 255) * (1 - fx) + ((b >> sh) & 255) * fx;
            double bot = ((c >> sh) & 255) * (1 - fx) + ((d >> sh) & 255) * fx;
            return (int)(top * (1 - fy) + bot * fy);
        }
        return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    /// <summary>Full-resolution, untouched capture. Only used for design snapshots.</summary>
    public static BitmapSource CaptureRaw(Int32Rect screen)
    {
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = screen.Width, biHeight = -screen.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc);
        IntPtr dib = CreateDIBSection(sdc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mdc, dib);
        StretchBlt(mdc, 0, 0, screen.Width, screen.Height, sdc, screen.X, screen.Y, screen.Width, screen.Height, SRCCOPY);
        var src = BitmapSource.Create(screen.Width, screen.Height, 96, 96, PixelFormats.Bgr32, null, bits, screen.Width * screen.Height * 4, screen.Width * 4);
        src.Freeze();
        SelectObject(mdc, old); DeleteObject(dib); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
        return src;
    }

    void EnsureGrab(int w, int h)
    {
        if (w == _gw && h == _gh && _memDc != IntPtr.Zero) return;
        FreeGdi();
        _gw = w; _gh = h;
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr sdc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(sdc);
        _dib = CreateDIBSection(sdc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, sdc);
        _old = SelectObject(_memDc, _dib);
        _tmp = new int[w * h];
        _work = new int[w * h];
        _lastHash = 0;
    }

    static void BoxH(int[] src, int[] dst, int w, int h, int r)
    {
        int div = r * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, sr = 0, sg = 0, sb = 0;
            for (int i = -r; i <= r; i++)
            {
                int p = src[row + Math.Clamp(i, 0, w - 1)];
                sr += (p >> 16) & 255; sg += (p >> 8) & 255; sb += p & 255;
            }
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (sr / div << 16) | (sg / div << 8) | (sb / div);
                int pOut = src[row + Math.Max(x - r, 0)], pIn = src[row + Math.Min(x + r + 1, w - 1)];
                sr += ((pIn >> 16) & 255) - ((pOut >> 16) & 255);
                sg += ((pIn >> 8) & 255) - ((pOut >> 8) & 255);
                sb += (pIn & 255) - (pOut & 255);
            }
        }
    }

    static void BoxV(int[] src, int[] dst, int w, int h, int r)
    {
        int div = r * 2 + 1;
        for (int x = 0; x < w; x++)
        {
            int sr = 0, sg = 0, sb = 0;
            for (int i = -r; i <= r; i++)
            {
                int p = src[Math.Clamp(i, 0, h - 1) * w + x];
                sr += (p >> 16) & 255; sg += (p >> 8) & 255; sb += p & 255;
            }
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (sr / div << 16) | (sg / div << 8) | (sb / div);
                int pOut = src[Math.Max(y - r, 0) * w + x], pIn = src[Math.Min(y + r + 1, h - 1) * w + x];
                sr += ((pIn >> 16) & 255) - ((pOut >> 16) & 255);
                sg += ((pIn >> 8) & 255) - ((pOut >> 8) & 255);
                sb += (pIn & 255) - (pOut & 255);
            }
        }
    }

    /// <summary>Vibrancy: boost saturation around the pixel's luma, like Apple's glass materials.</summary>
    static void Grade(int[] px, int n, double sat)
    {
        for (int i = 0; i < n; i++)
        {
            int p = px[i];
            double r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
            px[i] = (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
        }
    }

    static int Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

    double MeasureLuma(int[] px, int w, int h, Rect area)
    {
        // area is relative (0..1) to the captured rect
        int x0 = Math.Clamp((int)(area.X * w), 0, w - 1), x1 = Math.Clamp((int)(area.Right * w), x0 + 1, w);
        int y0 = Math.Clamp((int)(area.Y * h), 0, h - 1), y1 = Math.Clamp((int)(area.Bottom * h), y0 + 1, h);
        double sum = 0; int count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int p = px[y * w + x];
                sum += 0.2126 * ((p >> 16) & 255) + 0.7152 * ((p >> 8) & 255) + 0.0722 * (p & 255);
                count++;
            }
        return count == 0 ? Luminance : sum / count / 255.0;
    }

    void FreeGdi()
    {
        if (_memDc == IntPtr.Zero) return;
        SelectObject(_memDc, _old);
        DeleteObject(_dib);
        DeleteDC(_memDc);
        _memDc = _dib = _old = _bits = IntPtr.Zero;
    }

    /// <summary>Drops all buffers (liquid glass switched off).</summary>
    public void Release()
    {
        lock (_grabLock)
        {
            FreeGdi();
            _gw = _gh = 0;
            _tmp = _work = [];
        }
        lock (_gate)
        {
            _w = _h = 0;
            _blur = _out = [];
            _composedVersion = -1;
            Bitmap = null;
        }
    }

    public void Dispose()
    {
        lock (_grabLock) FreeGdi();
    }
}
