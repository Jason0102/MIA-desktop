using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Mia.Services;

internal static class PreviewAudio
{
    public static async Task<int> RunAsync(string root)
    {
        var settings = SettingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.ElevenLabsKey))
            SettingsStore.TryImportKeys(Path.Combine(root, "api_key"), settings, persist: false);
        if (string.IsNullOrWhiteSpace(settings.VoiceId))
        {
            using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "voice-profile.json")));
            settings.VoiceId = profile.RootElement.GetProperty("voice_id").GetString()!;
        }
        if (string.IsNullOrWhiteSpace(settings.ElevenLabsKey) || string.IsNullOrWhiteSpace(settings.VoiceId))
            throw new InvalidOperationException("請先在米亞設定中儲存 ElevenLabs 金鑰與聲線。");
        string folder = Path.Combine(root, "art", "speech-audio");
        Directory.CreateDirectory(folder);
        using var samples = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "samples.json")));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        foreach (var sample in samples.RootElement.EnumerateArray())
        {
            string code = sample.GetProperty("code").GetString()!;
            string tag = sample.GetProperty("tag").GetString()!;
            string line = sample.GetProperty("line").GetString()!;
            string text = tag.Length == 0 ? line : "[" + tag + "]" + line;
            string file = Path.Combine(folder, code + ".mp3");
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.VoiceId + settings.ElevenLabsModel + text)));
            if (File.Exists(file) && File.Exists(file + ".sha256") && await File.ReadAllTextAsync(file + ".sha256") == fingerprint) continue;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/text-to-dialogue?output_format=mp3_44100_128");
            request.Headers.Add("xi-api-key", settings.ElevenLabsKey);
            request.Content = JsonContent.Create(new { model_id = settings.ElevenLabsModel, inputs = new[] { new { text, voice_id = settings.VoiceId } } });
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { Console.WriteLine(code + " HTTP " + (int)response.StatusCode); return 1; }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 100 || response.Content.Headers.ContentType?.MediaType?.StartsWith("audio/") != true)
                throw new InvalidOperationException("語音回應格式錯誤。");
            await File.WriteAllBytesAsync(file + ".tmp", bytes);
            File.Move(file + ".tmp", file, true);
            await File.WriteAllTextAsync(file + ".sha256", fingerprint);
            Console.WriteLine(code + " ready");
        }
        return 0;
    }
}
