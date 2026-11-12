using System.Diagnostics;
using System.IO;

namespace NickControlCenter;

/// <summary>
/// Einfaches Selbst-Update: Liegt neben der exe eine „ControlCenter.update.exe“, tauscht die App sich aus
/// (eine laufende exe darf unter Windows umbenannt werden) und startet neu. So kann eine neue Version
/// auch dann eingespielt werden, während die App läuft.
/// </summary>
public static class SelfUpdate
{
    public static string UpdateFile => Path.Combine(Env.ExeDir, Path.GetFileNameWithoutExtension(Env.ExePath) + ".update.exe");
    private static string OldFile => Env.ExePath + ".old";

    /// <summary>Beim Start: alte Version wegräumen – und eine „.update.exe“, die schon genau dieser Version entspricht.</summary>
    public static void Cleanup()
    {
        try { if (File.Exists(OldFile)) File.Delete(OldFile); } catch { }
        try
        {
            if (!Env.ExePath.EndsWith(".update.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(UpdateFile) && SameFile(UpdateFile, Env.ExePath))
                for (int i = 0; i < 20; i++) { try { File.Delete(UpdateFile); break; } catch { Thread.Sleep(500); } }
        }
        catch { }
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var sa = File.Open(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sb = File.Open(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return sha.ComputeHash(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.Create().ComputeHash(sb));
    }

    /// <summary>
    /// Wurde die „.update.exe“ direkt per Doppelklick gestartet? Dann nicht als sie laufen (Verknüpfungen würden
    /// auf sie zeigen und sie könnte sich nie ersetzen), sondern: sich selbst als ControlCenter.exe einspielen und die starten.
    /// true = diese Instanz soll sich sofort beenden.
    /// </summary>
    public static bool RedirectIfStartedAsUpdate(bool mainInstanceRunning)
    {
        if (!Env.ExePath.EndsWith(".update.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var main = Env.ExePath[..^".update.exe".Length] + ".exe";
        if (mainInstanceRunning) return true; // die laufende App spielt das Update in wenigen Sekunden selbst ein
        try
        {
            if (File.Exists(main))
            {
                try { if (File.Exists(main + ".old")) File.Delete(main + ".old"); } catch { }
                File.Move(main, main + ".old");
            }
            File.Copy(Env.ExePath, main, true);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \"ping -n 2 127.0.0.1 >nul & start \"\" \"{main}\" --restart\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(main)! });
            return true;
        }
        catch
        {
            try { if (!File.Exists(main) && File.Exists(main + ".old")) File.Move(main + ".old", main); } catch { }
            return false; // dann eben als .update.exe weiterlaufen
        }
    }

    public static bool Pending => !Env.ExePath.EndsWith(".update.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(UpdateFile);

    /// <summary>Update einspielen und neu starten. true = App beendet sich gleich.</summary>
    public static bool Apply(bool tray)
    {
        if (!Pending) return false;
        try
        {
            // warten, bis die Datei fertig geschrieben ist
            long size = -1;
            for (int i = 0; i < 10; i++) { var s = new FileInfo(UpdateFile).Length; if (s == size && s > 1_000_000) break; size = s; Thread.Sleep(500); }
            Cleanup();
            SettingsBackup.Create("vor dem Update");
            File.Move(Env.ExePath, OldFile);
            File.Move(UpdateFile, Env.ExePath);
            Log.Ok(LogModule.App, "Neue Version eingespielt – App startet neu");
            var args = (tray ? " --tray" : "") + " --restart";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \"ping -n 3 127.0.0.1 >nul & start \"\" \"{Env.ExePath}\"{args}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Env.ExeDir });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogModule.App, "Update konnte nicht eingespielt werden: " + ex.Message);
            try { if (!File.Exists(Env.ExePath) && File.Exists(OldFile)) File.Move(OldFile, Env.ExePath); } catch { }
            return false;
        }
    }
}
