using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassIsland.Core;

public static class ColorTools
{
    /// <summary>Picks the most prominent vivid hue of an image (weighted hue histogram on a tiny copy).</summary>
    public static Color? Dominant(BitmapSource? src)
    {
        if (src == null) return null;
        try
        {
            BitmapSource small = src;
            double scale = 48.0 / Math.Max(src.PixelWidth, src.PixelHeight);
            if (scale < 1) small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            const int bins = 36;
            var weight = new double[bins];
            var sr = new double[bins]; var sg = new double[bins]; var sb = new double[bins];
            double tr = 0, tg = 0, tb = 0; int total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                tr += r; tg += g; tb += b; total++;
                ToHsv(r, g, b, out double hue, out double s, out double v);
                double wgt = s * s * v; // favour vivid, bright pixels
                if (s < 0.18 || v < 0.18) continue;
                int bin = (int)(hue / 360.0 * bins) % bins;
                weight[bin] += wgt; sr[bin] += r * wgt; sg[bin] += g * wgt; sb[bin] += b * wgt;
            }

            int best = 0;
            for (int i = 1; i < bins; i++)
            {
                double wi = weight[i] + 0.5 * (weight[(i + 1) % bins] + weight[(i + bins - 1) % bins]);
                double wb = weight[best] + 0.5 * (weight[(best + 1) % bins] + weight[(best + bins - 1) % bins]);
                if (wi > wb) best = i;
            }

            Color c = weight[best] > 0.5
                ? Color.FromRgb((byte)(sr[best] / weight[best] * 255), (byte)(sg[best] / weight[best] * 255), (byte)(sb[best] / weight[best] * 255))
                : Color.FromRgb((byte)(tr / total * 255), (byte)(tg / total * 255), (byte)(tb / total * 255));
            return Normalize(c);
        }
        catch { return null; }
    }

    /// <summary>Clamps saturation/brightness into a range that reads well as a UI accent.</summary>
    public static Color Normalize(Color c)
    {
        ToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double s, out double v);
        s = s < 0.12 ? s : Math.Clamp(s, 0.45, 0.85);
        v = Math.Clamp(v, 0.62, 0.95);
        return FromHsv(h, s, v);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
    public static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);

    public static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
