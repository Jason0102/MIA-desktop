using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Mia.Services;

internal static class VoiceExport
{
    internal static int Run(string id)
    {
        try
        {
            if (!Guid.TryParseExact(id, "N", out _) ||
                Environment.GetEnvironmentVariable("MIA_VOICE_EXPORT_ROOT") is not { Length: > 0 } root ||
                !Path.IsPathFullyQualified(root))
                throw new ArgumentException("語音輸出位置無效。");
            string folder = Path.Combine(root, id, "output");
            if (!Directory.Exists(folder)) throw new IOException("找不到語音輸出目錄。");
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            var input = new char[5001];
            int length = reader.ReadBlock(input, 0, input.Length);
            if (length == input.Length) throw new ArgumentException("語音稿最多 5000 字。");
            using var voice = new VoiceService(SettingsStore.Load());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            voice.ExportAsync(new string(input, 0, length), Path.Combine(folder, "MIA.mp3"), timeout.Token)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or HttpRequestException or OperationCanceledException)
        {
            string message = ex switch
            {
                OperationCanceledException => "語音生成逾時。",
                HttpRequestException http => $"ElevenLabs 請求失敗（{http.StatusCode?.ToString() ?? "無法連線"}）。",
                IOException => "無法儲存音訊，或語音服務未回傳有效音訊。",
                _ => ex.Message
            };
            using var output = Console.OpenStandardOutput();
            output.Write(JsonSerializer.SerializeToUtf8Bytes(new { error = message }));
            return 1;
        }
    }
}
