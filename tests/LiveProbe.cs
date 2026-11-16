using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mia.Services;

public static class LiveProbe
{
    public static async Task<int> RunAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "api_key"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Credential input file not found.");
        Environment.SetEnvironmentVariable("MIA_DATA_DIR", Path.Combine(directory.FullName, ".test-live"));
        var settings = SettingsStore.Load();
        if (!SettingsStore.TryImportKeys(Path.Combine(directory.FullName, "api_key"), settings))
            throw new InvalidOperationException("Credential import failed.");
        Console.WriteLine("Credential import: OpenAI=" + !string.IsNullOrEmpty(settings.OpenAiKey) + "; ElevenLabs=" + !string.IsNullOrEmpty(settings.ElevenLabsKey));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var ai = new AiService(settings);
        if (Environment.GetEnvironmentVariable("MIA_VISION_PROBE") == "1")
        {
            string screenshot = Path.Combine(directory.FullName, ".test-ui", "chat-preview.png");
            string resultState;
            string? errorType = null;
            try
            {
                byte[] image = await File.ReadAllBytesAsync(screenshot, timeout.Token);
                string answer = await ai.ReplyAsync("這是米亞應用程式自己的介面預覽圖。請用一句話描述介面標題。", image, timeout.Token);
                resultState = !string.IsNullOrWhiteSpace(answer) ? "pass" : "empty_response";
            }
            catch (Exception ex) { resultState = "failed"; errorType = ex.GetType().Name; }
            string report = JsonSerializer.Serialize(new
            {
                tested_utc = DateTimeOffset.UtcNow,
                model = "gpt-6.1-sol",
                input = ".test-ui/chat-preview.png (own application rendered preview)",
                desktop_capture = false,
                result = resultState,
                error_type = errorType
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "vision-check.json"), report);
            Console.WriteLine("OpenAI gpt-6.1-sol image input probe: " + resultState + "; rendered application preview only.");
            return resultState == "pass" ? 0 : 1;
        }
        try
        {
            var result = await ai.ReplyAsync("連線測試。請僅回覆：米亞連線成功。", null, timeout.Token);
            Console.WriteLine("OpenAI gpt-6.1-sol live reply: " + (!string.IsNullOrWhiteSpace(result) ? "PASS" : "EMPTY"));
        }
        catch (Exception ex) { Console.WriteLine("OpenAI probe: " + SafeError(ex)); }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (string endpoint in new[] { "models", "voices" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/" + endpoint);
                request.Headers.Add("xi-api-key", settings.ElevenLabsKey);
                using var response = await http.SendAsync(request, timeout.Token);
                Console.WriteLine("ElevenLabs " + endpoint + " HTTP: " + (int)response.StatusCode);
                if (!response.IsSuccessStatusCode) continue;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (endpoint == "models")
                {
                    bool v4 = json.RootElement.EnumerateArray().Any(x => x.TryGetProperty("model_id", out var id) && id.GetString() == "eleven_v4");
                    Console.WriteLine("ElevenLabs models contains eleven_v4: " + v4);
                }
                else
                {
                    bool valid = json.RootElement.TryGetProperty("voices", out var voices) && voices.ValueKind == JsonValueKind.Array;
                    Console.WriteLine("ElevenLabs voices response valid: " + valid);
                }
            }
            catch (Exception ex) { Console.WriteLine("ElevenLabs " + endpoint + " probe: " + SafeError(ex)); }
        }
        Console.WriteLine("No voice samples uploaded; no voice clone or paid TTS was generated.");
        return 0;
    }

    private static string SafeError(Exception error) => error switch
    {
        InvalidOperationException => error.Message,
        TimeoutException => "request timeout",
        OperationCanceledException => "request canceled or timed out",
        HttpRequestException => "network failure",
        _ => error.GetType().Name
    };
}
