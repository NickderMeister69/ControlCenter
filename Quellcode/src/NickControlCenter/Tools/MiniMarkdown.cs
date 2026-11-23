using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NickControlCenter;

/// <summary>Kleiner Markdown-Darsteller für KI-Antworten: Überschriften, Aufzählungen (auch eingerückt), **fett**, *kursiv*, `Code`.</summary>
public static class MiniMarkdown
{
    public static FlowDocument Build(string md, double fontSize = 13.5)
    {
        var text = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = fontSize, Foreground = text, FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), LineHeight = fontSize * 1.45 };
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();
            Paragraph p;
            var h = Regex.Match(t, @"^(#{1,4})\s+(.*)$");
            var bullet = Regex.Match(t, @"^([-*•])\s+(.*)$");
            var num = Regex.Match(t, @"^(\d+)[.)]\s+(.*)$");
            if (h.Success)
            {
                p = new Paragraph { Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = fontSize + (h.Groups[1].Length <= 2 ? 3 : 1.5), Foreground = accent };
                Inline(p, h.Groups[2].Value.Trim('*'), accent);
            }
            else if (bullet.Success || num.Success)
            {
                var level = Math.Min(3, indent / 2);
                p = new Paragraph { Margin = new Thickness(14 + level * 18, 2, 0, 2), TextIndent = -14 };
                p.Inlines.Add(new Run(num.Success ? num.Groups[1].Value + ".  " : (level == 0 ? "•  " : "◦  ")) { Foreground = accent, FontWeight = FontWeights.Bold });
                Inline(p, (bullet.Success ? bullet.Groups[2] : num.Groups[2]).Value, accent);
            }
            else
            {
                p = new Paragraph { Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : 6, 0, 2) };
                Inline(p, t, accent);
            }
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static void Inline(Paragraph p, string s, Brush accent)
    {
        foreach (Match m in Regex.Matches(s, @"\*\*(.+?)\*\*|`(.+?)`|(?<![\w*])\*(?!\s)(.+?)\*(?!\w)|([^*`]+|[*`])"))
        {
            if (m.Groups[1].Success) p.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold });
            else if (m.Groups[2].Success) p.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), Foreground = accent });
            else if (m.Groups[3].Success) p.Inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic });
            else p.Inlines.Add(new Run(m.Value));
        }
    }
}
