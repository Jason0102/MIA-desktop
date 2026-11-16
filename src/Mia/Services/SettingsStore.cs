using System;
using System.Linq;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mia.Services;

public static class SettingsStore
{
    public static string DataDirectory { get; } = GetDataDirectory();
    public static string? LastLoadWarning { get; private set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetDataDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("MIA_DATA_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIA Workspace", "state", "mia")
            : Path.GetFullPath(configured);
    }

    /// <summary>Imports an explicitly supplied credential file without logging values or deleting its source.</summary>
    public static bool TryImportKeys(string path, AppSettings settings, bool persist = true)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) return false;
        string openAi = "", eleven = "", pending = "";
        foreach (string original in File.ReadLines(path))
        {
            string line = original.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) line = line[1..^1].Trim();
            var match = Regex.Match(line, @"^(?<provider>open\s*ai|eleven\s*labs?)[\s_-]*(?:api[\s_-]*key)?\s*[:：=]?\s*(?<value>.*)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                pending = match.Groups["provider"].Value.StartsWith("open", StringComparison.OrdinalIgnoreCase) ? "openai" : "eleven";
                line = match.Groups["value"].Value.Trim();
                if (line.Length == 0) continue;
            }
            if (pending.Length > 0)
                line = Regex.Replace(line, @"^(?:api[\s_-]*key|key)\s*[:：=]\s*", "", RegexOptions.IgnoreCase);
            line = line.Trim().Trim('"', '\'');
            if (line.Length is < 16 or > 1024 || line.Any(char.IsWhiteSpace)) { pending = ""; continue; }
            if (pending == "openai") openAi = line;
            else if (pending == "eleven") eleven = line;
            pending = "";
        }
        if (openAi.Length == 0 && eleven.Length == 0) return false;
        if (openAi.Length > 0) settings.OpenAiKey = openAi;
        if (eleven.Length > 0) settings.ElevenLabsKey = eleven;
        if (persist) Save(settings);
        return true;
    }

    private sealed class StoredSettings
    {
        public AppSettings Settings { get; set; } = new();
        public string OpenAiSecret { get; set; } = "";
        public string ElevenLabsSecret { get; set; } = "";
    }

    public static AppSettings Load()
    {
        LastLoadWarning = null;
        var file = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(file)) return new();
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(file))
                         ?? throw new InvalidDataException();
            var settings = stored.Settings ?? new();
            if (settings.OpenAiModel == "gpt-6.1-terra") settings.OpenAiModel = "gpt-6.1-sol";
            settings.OpenAiKey = Unprotect(stored.OpenAiSecret);
            settings.ElevenLabsKey = Unprotect(stored.ElevenLabsSecret);
            settings.PetScale = double.IsFinite(settings.PetScale) ? Math.Clamp(settings.PetScale, 0.5, 3) : 1;
            settings.UiScale = double.IsFinite(settings.UiScale) ? Math.Clamp(settings.UiScale, 0.5, 3) : 1;
            settings.Mode=BallPlayRules.NormalizeMode(settings.Mode);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException)
        {
            LastLoadWarning = "設定檔無法讀取，已使用預設設定；請重新輸入 API Key。";
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.Mode=BallPlayRules.NormalizeMode(settings.Mode);
        Directory.CreateDirectory(DataDirectory);
        var stored = new StoredSettings
        {
            Settings = settings,
            OpenAiSecret = Protect(settings.OpenAiKey),
            ElevenLabsSecret = Protect(settings.ElevenLabsKey)
        };
        var target = Path.Combine(DataDirectory, "settings.json");
        var temporary = Path.Combine(DataDirectory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, JsonOptions), Encoding.UTF8);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var input = Encoding.UTF8.GetBytes(value.Trim());
        try { return Convert.ToBase64String(Transform(input, true)); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var output = Transform(Convert.FromBase64String(value), false);
        try { return Encoding.UTF8.GetString(output); }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    // DPAPI binds ciphertext to the current Windows user; no plaintext credentials enter JSON.
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var source = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob destination = default;
        try
        {
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            bool ok = encrypt
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination);
            if (!ok) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var result = new byte[destination.Length];
            Marshal.Copy(destination.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            if (destination.Data != IntPtr.Zero)
            {
                for (int i = 0; i < destination.Length; i++) Marshal.WriteByte(destination.Data, i, 0);
                LocalFree(destination.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
