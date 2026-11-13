using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mia.Services;

public static class VoiceSetup
{
    private const string VoiceName = "MIA Kiri";
    private const string Description = "MIA desktop companion; exclusively user-selected kiri_* materials.";
    private static readonly Dictionary<string, object> Report = new();
    private static string _reportPath = "";

    public static async Task<int> RunAsync()
    {
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project is not null && !File.Exists(Path.Combine(project.FullName, "api_key"))) project = project.Parent;
        if (project is null) throw new InvalidOperationException("Credential input file not found.");
        string data = Path.Combine(project.FullName, ".test-live");
        Environment.SetEnvironmentVariable("MIA_DATA_DIR", data);
        Directory.CreateDirectory(data);
        _reportPath = Path.Combine(data, "voice-setup-report.json");
        using var exclusive = new FileStream(Path.Combine(data, "voice-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var settings = SettingsStore.Load();
        if (!SettingsStore.TryImportKeys(Path.Combine(project.FullName, "api_key"), settings))
            throw new InvalidOperationException("Credential import failed.");
        string folder = Path.Combine(project.Parent!.FullName, "voice", "materials");
        var files = VoiceService.FindKiriMaterials(folder);
        Report["started_utc"] = DateTimeOffset.UtcNow;
        Report["material_files"] = files.Select(Path.GetFileName).ToArray();
        Report["material_bytes"] = files.Sum(p => new FileInfo(p).Length);
        Report["model"] = "eleven_v4";
        SaveReport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Add("xi-api-key", settings.ElevenLabsKey);
        try
        {
            using var response = await http.GetAsync("https://api.elevenlabs.io/v1/voices", timeout.Token);
            Report["voice_lookup_http"] = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return await FailureAsync(response, "voice_lookup", timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            string? voiceId = null;
            foreach (var voice in json.RootElement.GetProperty("voices").EnumerateArray())
            {
                if (voice.TryGetProperty("name", out var name) && name.GetString() == VoiceName
                    && voice.TryGetProperty("description", out var description) && description.GetString() == Description)
                { voiceId = voice.GetProperty("voice_id").GetString(); break; }
            }
            Report["reused_exact_matching_voice"] = !string.IsNullOrWhiteSpace(voiceId);
            if (string.IsNullOrWhiteSpace(voiceId))
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(VoiceName), "name");
                form.Add(new StringContent(Description), "description");
                foreach (var file in files)
                {
                    var content = new StreamContent(File.OpenRead(file));
                    content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                    form.Add(content, "files", Path.GetFileName(file));
                }
                // Exactly one POST, with no retries and no invented speaker-rights attestation.
                Report["clone_post_attempted"] = true;
                SaveReport();
                using var created = await http.PostAsync("https://api.elevenlabs.io/v1/voices/add", form, timeout.Token);
                Report["clone_http"] = (int)created.StatusCode;
                if (!created.IsSuccessStatusCode) return await FailureAsync(created, "clone", timeout.Token);
                using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(timeout.Token));
                voiceId = createdJson.RootElement.GetProperty("voice_id").GetString();
                bool verification = createdJson.RootElement.TryGetProperty("requires_verification", out var verify) && verify.ValueKind == JsonValueKind.True;
                Report["requires_verification"] = verification;
                settings.VoiceId = voiceId ?? throw new InvalidDataException("Voice ID missing.");
                settings.VoiceMaterialsPath = folder;
                SettingsStore.Save(settings);
                Report["voice_id_saved"] = true;
                SaveReport();
                if (verification)
                {
                    Report["result"] = "voice_created_verification_required_no_synthesis_attempted";
                    SaveReport(); Console.WriteLine("Voice created; provider verification required. Synthesis skipped.");
                    return 2;
                }
            }
            settings.VoiceId = voiceId!;
            settings.VoiceMaterialsPath = folder;
            SettingsStore.Save(settings);
            Report["voice_id_saved"] = true;
            using var body = JsonContent.Create(new
            {
                model_id = "eleven_v4",
                inputs = new[] { new { text = "你好，我是米亞，很高興陪著你。", voice_id = voiceId } }
            });
            using var spoken = await http.PostAsync("https://api.elevenlabs.io/v1/text-to-dialogue?output_format=mp3_44100_128", body, timeout.Token);
            Report["tts_http"] = (int)spoken.StatusCode;
            if (!spoken.IsSuccessStatusCode) return await FailureAsync(spoken, "tts", timeout.Token);
            string mime = spoken.Content.Headers.ContentType?.MediaType ?? "";
            byte[] bytes = await spoken.Content.ReadAsByteArrayAsync(timeout.Token);
            bool mp3 = bytes.Length > 100 && ((bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
                || (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0));
            Report["audio_content_type"] = mime;
            Report["audio_bytes"] = bytes.Length;
            Report["audio_mp3_signature_valid"] = mp3;
            if (!mp3 || !(mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || mime == "application/octet-stream"))
                throw new InvalidDataException("Unexpected audio response format.");
            await File.WriteAllBytesAsync(Path.Combine(data, "mia-kiri-v4-test.mp3"), bytes, timeout.Token);
            Report["result"] = "voice_ready_synthesis_pass";
            SaveReport();
            Console.WriteLine("MIA Kiri voice ready; exactly kiri_* material selected; Eleven v4 test audio validated and saved.");
            Console.WriteLine("Reused voice: " + Report["reused_exact_matching_voice"] + "; audio bytes: " + bytes.Length);
            return 0;
        }
        catch (Exception ex)
        {
            Report["result"] = "request_failed_no_automatic_retry";
            Report["error_type"] = ex.GetType().Name;
            SaveReport();
            Console.WriteLine("Voice setup stopped: " + ex.GetType().Name + ". No automatic retry.");
            return 1;
        }
    }

    private static async Task<int> FailureAsync(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        Report["result"] = stage + "_failed";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                if (detail.TryGetProperty("status", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    string safeCode = code.GetString() ?? "";
                    if (Regex.IsMatch(safeCode, "^[a-zA-Z0-9_-]{1,80}$")) Report["provider_error_code"] = safeCode;
                }
                if (detail.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    string text = message.GetString() ?? "";
                    Report["rights_or_verification_mentioned"] = Regex.IsMatch(text, "consent|right|verif|permission", RegexOptions.IgnoreCase);
                }
            }
        }
        catch (JsonException) { }
        SaveReport();
        Console.WriteLine(stage + " HTTP " + (int)response.StatusCode + "; setup stopped, no automatic retry.");
        if (Report.TryGetValue("provider_error_code", out var value)) Console.WriteLine("Provider code: " + value);
        return 2;
    }

    private static void SaveReport() => File.WriteAllText(_reportPath, JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
}
