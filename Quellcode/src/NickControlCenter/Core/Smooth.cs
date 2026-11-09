using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

/// <summary>Eine Zahl, die weich auf ihren Zielwert zugleitet statt zu springen.</summary>
public sealed class SmoothNumber
{
    /// <summary>Anteil pro Bild (40 ms), um den sich der Wert dem Ziel nähert → ca. 1 s bis zum Ziel.</summary>
    public const double Rate = 0.12;
    public double Current { get; private set; } = double.NaN;
    public double Target { get; private set; } = double.NaN;

    public void Set(double v)
    {
        Target = v;
        // erster Wert oder Wert fällt weg → sofort übernehmen
        if (double.IsNaN(Current) || double.IsNaN(v)) Current = v;
    }

    /// <summary>Einen Schritt Richtung Ziel. true = Wert hat sich geändert.</summary>
    public bool Step()
    {
        if (double.IsNaN(Target) || double.IsNaN(Current)) return false;
        double d = Target - Current;
        if (Math.Abs(d) < 0.02 * Math.Max(1, Math.Abs(Target) / 100))
        {
            if (Current == Target) return false;
            Current = Target;
            return true;
        }
        Current += d * Rate;
        return true;
    }
}

/// <summary>
/// Gemeinsamer Takt (25 Bilder/s) für alle weich laufenden Zahlen der App – Dashboard, Monitor, Kacheln, Overlay, Lüfter.
/// </summary>
public static class Smooth
{
    private sealed class Entry
    {
        public readonly SmoothNumber N = new();
        public Func<double, string> Format = v => v.ToString("0");
        public WeakReference<TextBlock>? Tb;
    }

    private static readonly ConditionalWeakTable<TextBlock, Entry> Map = new();
    private static readonly List<Entry> Entries = new();
    private static DispatcherTimer? _timer;
    private static event Action? _frame;

    /// <summary>Wird 25× pro Sekunde im UI-Thread ausgelöst.</summary>
    public static event Action? Frame { add { Ensure(); _frame += value; } remove => _frame -= value; }

    private static void Ensure()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Text einer Zahl weich auf den neuen Wert laufen lassen.</summary>
    public static void Text(TextBlock tb, double value, Func<double, string> format)
    {
        Ensure();
        if (!Map.TryGetValue(tb, out var e))
        {
            e = new Entry { Tb = new WeakReference<TextBlock>(tb) };
            Map.Add(tb, e);
            Entries.Add(e);
        }
        e.Format = format;
        bool snap = double.IsNaN(e.N.Current) || double.IsNaN(value);
        e.N.Set(value);
        if (snap) tb.Text = format(value);
    }

    private static void Tick()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Tb == null || !e.Tb.TryGetTarget(out var tb)) { Entries.RemoveAt(i); continue; }
            if (e.N.Step()) tb.Text = e.Format(e.N.Current);
        }
        _frame?.Invoke();
    }
}
