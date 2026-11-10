using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NickControlCenter;

public sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Changed { get; set; } = DateTime.Now;
}

/// <summary>Verschlüsselte Datei: nur Salt, Rundenzahl, Nonce, Tag und Chiffrat – nichts im Klartext.</summary>
public sealed class VaultFile
{
    public int Version { get; set; } = 1;
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>
/// Passwort-Tresor: AES-256-GCM (verschlüsselt und erkennt jede Veränderung), Schlüssel aus dem Master-Passwort
/// über PBKDF2-SHA256 mit 600.000 Runden (OWASP-Empfehlung) und zufälligem Salt. Das Master-Passwort wird nirgends
/// gespeichert – wer es vergisst, kommt nicht mehr an die Daten (auch nicht über die App).
/// </summary>
public sealed class Vault
{
    public const int DefaultIterations = 600_000;
    private byte[]? _key;
    private byte[] _salt = Array.Empty<byte>();
    private int _iterations = DefaultIterations;
    public List<VaultEntry> Entries { get; private set; } = new();
    public bool Unlocked => _key != null;
    public string Path { get; }

    public Vault(string path) { Path = path; }
    public bool Exists => File.Exists(Path);

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public void Create(string password, int iterations = DefaultIterations)
    {
        if (Exists) throw new InvalidOperationException("Es gibt schon einen Tresor.");
        _salt = RandomNumberGenerator.GetBytes(16);
        _iterations = iterations;
        _key = Derive(password, _salt, _iterations);
        Entries = new();
        Save();
    }

    /// <summary>false = falsches Passwort (oder Datei verändert).</summary>
    public bool Unlock(string password)
    {
        var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(Path)) ?? throw new InvalidDataException("Tresor-Datei ist leer.");
        var salt = Convert.FromBase64String(f.Salt);
        var key = Derive(password, salt, f.Iterations);
        try
        {
            var plain = Decrypt(key, f);
            Entries = JsonSerializer.Deserialize<List<VaultEntry>>(plain) ?? new();
            CryptographicOperations.ZeroMemory(plain);
            _key = key; _salt = salt; _iterations = f.Iterations;
            return true;
        }
        catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(key); return false; }
    }

    private static byte[] Decrypt(byte[] key, VaultFile f)
    {
        var nonce = Convert.FromBase64String(f.Nonce);
        var tag = Convert.FromBase64String(f.Tag);
        var cipher = Convert.FromBase64String(f.Data);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public void Save()
    {
        if (_key == null) throw new InvalidOperationException("Tresor ist gesperrt.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(Entries);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);
        var f = new VaultFile
        {
            Iterations = _iterations, Salt = Convert.ToBase64String(_salt), Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher)
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".neu";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true); // letzte Version als Sicherung
        File.Move(tmp, Path, true);
    }

    public bool ChangePassword(string oldPassword, string newPassword)
    {
        var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(Path))!;
        var check = Derive(oldPassword, Convert.FromBase64String(f.Salt), f.Iterations);
        try { CryptographicOperations.ZeroMemory(Decrypt(check, f)); }
        catch (AuthenticationTagMismatchException) { return false; }
        _salt = RandomNumberGenerator.GetBytes(16);
        _iterations = DefaultIterations;
        _key = Derive(newPassword, _salt, _iterations);
        Save();
        return true;
    }

    public void Lock()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        Entries = new();
    }

    // ---------------- Hilfen
    private const string Lower = "abcdefghijkmnopqrstuvwxyz", Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", Digits = "23456789", Symbols = "!#$%&*+-=?@_~.";

    /// <summary>Zufälliges Passwort (kryptografisch sicher, ohne verwechselbare Zeichen wie l/1/O/0).</summary>
    public static string Generate(int length = 20, bool symbols = true)
    {
        length = Math.Clamp(length, 8, 128);
        var sets = new List<string> { Lower, Upper, Digits };
        if (symbols) sets.Add(Symbols);
        var all = string.Concat(sets);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // aus jeder Gruppe mindestens ein Zeichen
        for (int s = 0; s < sets.Count; s++) chars[RandomNumberGenerator.GetInt32(length)] = sets[s][RandomNumberGenerator.GetInt32(sets[s].Length)];
        return new string(chars);
    }

    /// <summary>Stärke 0–4 (grob über die Entropie) mit Text.</summary>
    public static (int Score, string Text) Strength(string pw)
    {
        if (string.IsNullOrEmpty(pw)) return (0, "");
        int pool = 0;
        if (pw.Any(char.IsLower)) pool += 26;
        if (pw.Any(char.IsUpper)) pool += 26;
        if (pw.Any(char.IsDigit)) pool += 10;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) pool += 32;
        double bits = pw.Length * Math.Log2(Math.Max(pool, 1));
        if (pw.Distinct().Count() <= 3) bits = Math.Min(bits, 20);
        return bits switch
        {
            < 40 => (1, "schwach"),
            < 60 => (2, "mittel"),
            < 80 => (3, "stark"),
            _ => (4, "sehr stark"),
        };
    }
}
