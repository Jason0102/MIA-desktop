using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mia.Services;

public static class ServiceChecks
{
    public static async Task RunAsync()
    {
        // Program initializes MIA_DATA_DIR before the first SettingsStore access.
        string temporary = Path.Combine(SettingsStore.DataDirectory, "additional-service-checks");
        Directory.CreateDirectory(temporary);
        var settings = new AppSettings();
        string credentials = Path.Combine(temporary, "test-keys.txt");
        const string openAi = "sk-test-openai-never-a-real-key-123456";
        const string eleven = "sk-test-eleven-never-a-real-key-12345";
        File.WriteAllText(credentials, "[elevenlabs]\napi_key = " + eleven + "\n\n[openai]\napi_key = " + openAi);
        Check(SettingsStore.TryImportKeys(credentials, settings), "credential import succeeds");
        Check(settings.OpenAiKey == openAi && settings.ElevenLabsKey == eleven, "label-based import maps keys");
        Check(!JsonSerializer.Serialize(settings).Contains(openAi), "public settings serialization omits keys");
        settings.VoiceId = "test-voice";

        string? lastRequest = null;
        int mode = 0;
        AiService? ai = null;
        using (ai = new AiService(settings, new FakeHandler(async (request, ct) =>
        {
            lastRequest = await request.Content!.ReadAsStringAsync(ct);
            if (mode == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (mode == 2) await Task.Delay(Timeout.Infinite, ct);
            if (mode == 3) ai!.ClearHistory();
            return Json("""{"output":[{"type":"message","content":[{"type":"output_text","text":"你好，我是米亞。"}]}]}""");
        })))
        {
            ai.ClearHistory();
            await ai.ReplyAsync("畫面內容？", [1, 2, 3], CancellationToken.None);
            using (var request = JsonDocument.Parse(lastRequest!))
            {
                var root = request.RootElement;
                Check(root.GetProperty("input")[2].GetProperty("content")[1].GetProperty("image_url").GetString() == "data:image/png;base64,AQID", "image encoding contract");
                Check(root.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search", "only web search enabled, no desktop tools");
                Check(root.GetProperty("prompt_cache_options").GetProperty("mode").GetString() == "implicit", "prompt caching enabled");
                Check(!root.TryGetProperty("max_output_tokens", out _), "no artificial 1800 token ceiling");
            }
            mode = 1;
            await ExpectAsync<InvalidOperationException>(() => ai.ReplyAsync("failed-turn-marker", null, CancellationToken.None));
            mode = 2;
            using (var canceled = new CancellationTokenSource(20))
                await ExpectAsync<OperationCanceledException>(() => ai.ReplyAsync("canceled-turn-marker", null, canceled.Token));
            mode = 0;
            await ai.ReplyAsync("下一句", null, CancellationToken.None);
            Check(!lastRequest!.Contains("failed-turn-marker") && !lastRequest.Contains("canceled-turn-marker"), "failed and canceled turns absent from history");
            for (int i = 0; i < 20; i++) await ai.ReplyAsync("history-" + i, null, CancellationToken.None);
            using (var request = JsonDocument.Parse(lastRequest!))
                Check(request.RootElement.GetProperty("input").GetArrayLength() <= 403, "conversation history bounded including per-turn clock");
            mode = 3;
            await ai.ReplyAsync("clear-during-turn-marker", null, CancellationToken.None);
            mode = 0;
            await ai.ReplyAsync("新的對話", null, CancellationToken.None);
            using (var request = JsonDocument.Parse(lastRequest!))
                Check(request.RootElement.GetProperty("input").GetArrayLength() == 3, "clear during request prevents stale history restore");
        }
        string materialFolder = Path.Combine(temporary, "materials");
        Directory.CreateDirectory(materialFolder);
        File.WriteAllBytes(Path.Combine(materialFolder, "kiri_general.mp3"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(materialFolder, "nested"));
        File.WriteAllBytes(Path.Combine(materialFolder, "nested", "kiri_hidden.mp3"), [7]);
        Check(VoiceService.FindKiriMaterials(materialFolder).Count == 1, "voice selection stays nonrecursive");
        string? voiceRequest = null;
        using var voice = new VoiceService(settings, new FakeHandler(async (request, ct) =>
        {
            voiceRequest = await request.Content!.ReadAsStringAsync(ct);
            Check(request.RequestUri!.AbsolutePath == "/v1/text-to-dialogue/stream", "Eleven v4 dialogue endpoint");
            // Reject before playback; no audio hardware or cloud calls are needed.
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        await ExpectAsync<InvalidOperationException>(() => voice.SpeakAsync("測試語音", CancellationToken.None));
        using (var request = JsonDocument.Parse(voiceRequest!))
        {
            Check(request.RootElement.GetProperty("model_id").GetString() == "eleven_v4", "no silent voice-model substitution");
            Check(request.RootElement.GetProperty("inputs")[0].GetProperty("voice_id").GetString() == "test-voice", "selected voice sent");
        }
        File.WriteAllBytes(Path.Combine(materialFolder, "kiri_empty.mp3"), []);
        await ExpectAsync<InvalidOperationException>(() => Task.FromResult(VoiceService.FindKiriMaterials(materialFolder)));
        Console.WriteLine("PASS additional cloud contracts, history races, secure import, v4 endpoint and material limits");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException("Service check failed: " + name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct);
    }
}
