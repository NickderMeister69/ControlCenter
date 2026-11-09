using System.Text;

namespace NickControlCenter;

/// <summary>
/// Kleiner INI-Editor, der Datei-Inhalt, Kodierung und Reihenfolge erhält (wichtig für Unreal-Configs wie bei ARK).
/// Setzt nur die gewünschten Schlüssel – alles andere bleibt Zeichen für Zeichen gleich.
/// </summary>
public sealed class IniFile
{
    public List<string> Lines { get; } = new();
    public Encoding Encoding { get; private set; } = new UTF8Encoding(false);

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;
        var bytes = File.ReadAllBytes(path);
        ini.Encoding = Detect(bytes);
        var text = ini.Encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        ini.Lines.AddRange(text.Split('\n').Select(l => l.TrimEnd('\r')));
        while (ini.Lines.Count > 0 && string.IsNullOrWhiteSpace(ini.Lines[^1])) ini.Lines.RemoveAt(ini.Lines.Count - 1);
        return ini;
    }

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        ini.Lines.AddRange(text.Replace("\r", "").Split('\n'));
        while (ini.Lines.Count > 0 && string.IsNullOrWhiteSpace(ini.Lines[^1])) ini.Lines.RemoveAt(ini.Lines.Count - 1);
        return ini;
    }

    private static Encoding Detect(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    private int FindSection(string section)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            var t = Lines[i].Trim();
            if (t.Length > 2 && t[0] == '[' && t[^1] == ']' && string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private int SectionEnd(int start)
    {
        for (int j = start + 1; j < Lines.Count; j++)
            if (Lines[j].TrimStart().StartsWith('[')) return j;
        return Lines.Count;
    }

    private static bool IsKey(string line, string key)
    {
        int eq = line.IndexOf('=');
        return eq > 0 && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase);
    }

    public string? Get(string section, string key)
    {
        int s = FindSection(section);
        if (s < 0) return null;
        int end = SectionEnd(s);
        for (int j = s + 1; j < end; j++)
            if (IsKey(Lines[j], key)) return Lines[j][(Lines[j].IndexOf('=') + 1)..].Trim();
        return null;
    }

    public void Set(string section, string key, string value)
    {
        int s = FindSection(section);
        if (s < 0)
        {
            if (Lines.Count > 0 && !string.IsNullOrWhiteSpace(Lines[^1])) Lines.Add("");
            Lines.Add($"[{section}]");
            Lines.Add($"{key}={value}");
            return;
        }
        int end = SectionEnd(s);
        for (int j = s + 1; j < end; j++)
        {
            if (!IsKey(Lines[j], key)) continue;
            Lines[j] = $"{key}={value}";
            return;
        }
        int ins = end;
        while (ins > s + 1 && string.IsNullOrWhiteSpace(Lines[ins - 1])) ins--;
        Lines.Insert(ins, $"{key}={value}");
    }

    public bool Remove(string section, string key)
    {
        int s = FindSection(section);
        if (s < 0) return false;
        int end = SectionEnd(s);
        for (int j = s + 1; j < end; j++)
            if (IsKey(Lines[j], key)) { Lines.RemoveAt(j); return true; }
        return false;
    }

    public override string ToString() => string.Join("\r\n", Lines) + "\r\n";

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var fi = new FileInfo(path);
        bool ro = fi.Exists && fi.IsReadOnly;
        if (ro) fi.IsReadOnly = false;
        try { File.WriteAllText(path, ToString(), Encoding); }
        finally { if (ro) new FileInfo(path).IsReadOnly = true; }
    }
}
