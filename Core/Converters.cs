using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GlassIsland.Core;

/// <summary>Attached icon glyph, used by templates (e.g. the search box icon).</summary>
public static class Glyph
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached("Value", typeof(string), typeof(Glyph), new PropertyMetadata(""));
    public static string GetValue(DependencyObject o) => (string)o.GetValue(ValueProperty);
    public static void SetValue(DependencyObject o, string v) => o.SetValue(ValueProperty, v);
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c) => (value != null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>True when the bound value equals the parameter (string compare). Used for segmented controls.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Progress (0..1) → ring arc geometry. ConverterParameter = "diameter,thickness".</summary>
public sealed class ArcConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var parts = (p as string ?? "100,8").Split(',');
        double d = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double th = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 8;
        double progress = Math.Clamp(value is double v ? v : 0, 0, 0.9999);
        double r = (d - th) / 2, cx = d / 2, cy = d / 2;
        if (progress <= 0.0001) return Geometry.Empty;
        double angle = progress * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, progress > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
