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

    /// <summary>Beim Start: alte Version wegräumen.</summary>
    public static void Cleanup()
    {
        try { if (File.Exists(OldFile)) File.Delete(OldFile); } catch { }
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
