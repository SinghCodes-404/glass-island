using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GlassIsland.Core;

/// <summary>
/// Tiny Markdown subset for chat bubbles: **bold**, *italic*, `code` and "- " bullets.
/// Usage: &lt;TextBlock core:RichText.Markdown="{Binding Text}" /&gt;
/// </summary>
public static partial class RichText
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(RichText), new PropertyMetadata("", (d, e) => Render(d as TextBlock, e.NewValue as string)));

    public static string GetMarkdown(DependencyObject o) => (string)o.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject o, string v) => o.SetValue(MarkdownProperty, v);

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)));

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)")]
    private static partial Regex Inline();

    static void Render(TextBlock? tb, string? text)
    {
        if (tb == null) return;
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) tb.Inlines.Add(new LineBreak());
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                tb.Inlines.Add(new Run("  •  "));
                line = trimmed[2..];
            }
            else if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#', ' ');   // no headings: show as plain text
            AddInline(tb.Inlines, line);
        }
    }

    static void AddInline(InlineCollection target, string line)
    {
        int pos = 0;
        foreach (Match m in Inline().Matches(line))
        {
            if (m.Index > pos) target.Add(new Run(line[pos..m.Index]));
            if (m.Groups[1].Success) target.Add(new Bold(new Run(m.Groups[1].Value)));
            else if (m.Groups[2].Success) target.Add(new Run(" " + m.Groups[2].Value + " ") { FontFamily = Mono, Background = CodeBackground });
            else target.Add(new Italic(new Run(m.Groups[3].Value)));
            pos = m.Index + m.Length;
        }
        if (pos < line.Length) target.Add(new Run(line[pos..]));
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
