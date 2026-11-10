using System.Runtime.InteropServices;

namespace NickControlCenter;

/// <summary>
/// Versteckt die Fenster aller Unterprozesse eines Prozesses (z. B. Installer, die winget trotz „--silent“ mit
/// Fortschrittsfenster startet). Läuft ein Installer ungewöhnlich lange, werden seine Fenster wieder gezeigt –
/// falls er doch eine Frage stellt.
/// </summary>
public sealed class WindowHider : IDisposable
{
    private readonly int _root;
    private readonly Timer _timer;
    private readonly HashSet<IntPtr> _hidden = new();
    private readonly DateTime _start = DateTime.Now;
    private readonly TimeSpan _showAfter;
    private bool _revealed;

    public WindowHider(int rootPid, TimeSpan showAfter)
    {
        _root = rootPid; _showAfter = showAfter;
        _timer = new Timer(_ => Tick(), null, 0, 200);
    }

    private void Tick()
    {
        try
        {
            if (DateTime.Now - _start > _showAfter)
            {
                if (!_revealed) { _revealed = true; lock (_hidden) foreach (var h in _hidden) ShowWindow(h, 4); Log.Info(LogModule.App, "Installer braucht ungewöhnlich lange – Fenster wird angezeigt"); }
                return;
            }
            var tree = Descendants(_root);
            if (tree.Count == 0) return;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!tree.Contains((int)pid)) return true;
                ShowWindow(h, 0); // SW_HIDE
                lock (_hidden) _hidden.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>Alle Nachfahren eines Prozesses (Toolhelp-Snapshot).</summary>
    private static HashSet<int> Descendants(int root)
    {
        var parent = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new();
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
                do parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        var res = new HashSet<int>();
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var (pid, par) in parent)
                if ((par == root || res.Contains(par)) && pid != root && res.Add(pid)) added = true;
        }
        return res;
    }

    public void Dispose() => _timer.Dispose();

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
}
