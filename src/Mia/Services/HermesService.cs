using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Mia.Services;

public sealed record HermesControl(string RunId, string Kind, string RequestId, int? Generation, string Text, bool AllowSession = false, bool AllowAlways = false);
public sealed record HermesUpdate(string Event, string Text = "", string Tool = "", HermesControl? Control = null);

public sealed class HermesService : IDisposable
{
    private readonly Uri _baseAddress;
    private readonly string _key;
    private readonly HttpClient _http;
    private readonly Func<Task> _resetBuiltInMemory;
    private readonly ConversationStore _conversation = new("hermes-conversation.jsonl");
    private readonly string _sessionFile = Path.Combine(SettingsStore.DataDirectory, "hermes-session.txt");
    private readonly RoleProfile _role = RoleProfile.Load(Path.Combine(AppContext.BaseDirectory, "role.json"));
    private string? _sessionId;
    private readonly object _historyLock = new();
    public IReadOnlyList<ConversationTurn> History { get; private set; }
    public string? MemoryWarning { get; private set; }

    public HermesService(Uri baseAddress, string key, HttpMessageHandler? handler = null,
        Func<Task>? resetBuiltInMemory = null)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != "http" || baseAddress.Host != "127.0.0.1"
            || baseAddress.AbsolutePath != "/" || baseAddress.UserInfo.Length != 0
            || baseAddress.Query.Length != 0 || baseAddress.Fragment.Length != 0)
            throw new ArgumentException("Hermes 網址必須使用本機 http://127.0.0.1:連接埠。");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("Hermes 本機連線金鑰無效，請由 3.2 啟動程式重新啟動。");

        _baseAddress = baseAddress;
        _key = key;
        _resetBuiltInMemory = resetBuiltInMemory ?? ResetBuiltInMemoryAsync;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        try
        {
            History = _conversation.LoadRecent();
            if (File.Exists(_sessionFile) && new FileInfo(_sessionFile).Length <= 128)
            {
                string saved = File.ReadAllText(_sessionFile).Trim();
                if (Guid.TryParseExact(saved, "N", out _)) _sessionId = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MemoryWarning = "無法讀取米亞的工作對話紀錄。";
        }
        History ??= Array.Empty<ConversationTurn>();
    }

    public static HermesService? FromEnvironment()
    {
        string? url = Environment.GetEnvironmentVariable("MIA_HERMES_URL");
        string? key = Environment.GetEnvironmentVariable("MIA_HERMES_KEY");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("API_SERVER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)) return null;
            url = "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("API_SERVER_PORT") ?? "8642");
            key = Environment.GetEnvironmentVariable("API_SERVER_KEY");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("Hermes 本機連線網址無效，請由 3.2 啟動程式重新啟動。");
        return new HermesService(uri, key ?? "");
    }

    public async Task<string> ReplyAsync(string text, IReadOnlyList<byte[]> images, CancellationToken cancellationToken,
        Func<HermesUpdate, Task> onUpdate, bool independentTask = false,
        string? taskSession = null, string? additionalInstructions = null, bool saveHistory = true,
        string reasoningEffort = "medium", MoodSnapshot? mood = null)
    {
        if (reasoningEffort is not ("low" or "medium" or "high")) throw new ArgumentException("無效的推理強度。");
        cancellationToken.ThrowIfCancellationRequested();
        if (images.Sum(image => ((long)image.Length + 2) / 3 * 4) > 9_000_000)
            throw new ArgumentException("Hermes 的附圖總容量超出限制，請減少附圖或縮小圖片。");

        string sessionId = taskSession ?? (independentTask ? Guid.NewGuid().ToString("N") : _sessionId ??= Guid.NewGuid().ToString("N"));
        if (!independentTask && taskSession is null) SaveSession(sessionId);
        string instructions = _role.Instructions + "\n" + RoleProfile.TimeContext(DateTimeOffset.Now, TimeZoneInfo.Local.Id) + "\n" + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "task-policy.md"));
        instructions += "\n" + UserIdentity.Context("desktop", "local") + "\n" + SpeechTags.Instructions +
            "\n" + InteractionMood.Prompt(mood ?? InteractionMood.Desktop(), text);
        if (additionalInstructions is not null) instructions += "\n" + additionalInstructions;
        object input = text;
        if (images.Count > 0)
        {
            var content = new List<object> { new { type = "text", text } };
            content.AddRange(images.Select(image => new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) } }));
            input = new[] { new { role = "user", content } };
        }

        string? runPath = null;
        bool settled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        try
        {
            using (var request = Request(HttpMethod.Post, "v1/runs", new { input, session_id = sessionId, instructions, model_options = new { reasoning_effort = reasoningEffort } }))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                // Finish reading the admission receipt even if Stop is clicked mid-request.
                // The run ID lets us stop the accepted server run rather than just dropping the stream.
                using var response = await _http.SendAsync(request, CancellationToken.None);
                CheckResponse(response);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
                string id = GetString(doc.RootElement, "run_id");
                if (id.Length is < 1 or > 128 || id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
                    throw new IOException("Hermes 未回傳有效的工作編號。");
                runPath = "v1/runs/" + Uri.EscapeDataString(id);
                await onUpdate(new("run.accepted", runPath));
                ct.ThrowIfCancellationRequested();
            }

            string? output = await ReadRunAsync(runPath, onUpdate, ct);
            if (output is null)
                output = await PollRunAsync(runPath, onUpdate, ct);
            settled = true;

            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(output)) throw new IOException("Hermes 完成工作，但未回傳文字成果。");
            if (saveHistory && (taskSession is not null || independentTask || _sessionId == sessionId))
            {
                try { lock (_historyLock) _conversation.Append(images.Count == 0 ? text : $"{text}（本次附有 {images.Count} 張畫面）", output); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { MemoryWarning = "工作已完成，但米亞無法儲存本次對話。"; }
            }
            return output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!settled && runPath is not null)
            {
                bool stopAccepted = await RequestStopAsync(runPath);
                await onUpdate(new(stopAccepted ? "run.stop_requested" : "run.stop_unconfirmed"));
                if (!stopAccepted) throw new IOException("無法確認 Hermes 已收到停止要求，請查看 Hermes 中的工作狀態。");
            }
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("Hermes 工作超過 30 分鐘，已結束等待。");
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new IOException(runPath is null
                ? "無法連線到本機 Hermes，請檢查 3.2 的 Gateway 是否已啟動。"
                : "Hermes 工作連線中斷。請查看原工作的狀態，再決定是否重送。", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Hermes 本機 API 未及時回應，請先檢查 Gateway 的工作狀態。", ex);
        }
    }

    public void RecordExchange(string text, string reply)
    {
        lock (_historyLock) _conversation.Append(text, reply);
    }

    public async Task<bool> RememberPreferenceAsync(string content, string oldText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? executable = Environment.GetEnvironmentVariable("MIA_HERMES_EXECUTABLE");
        string? entry = Environment.GetEnvironmentVariable("MIA_HERMES_MEMORY_ENTRY");
        if (!File.Exists(executable) || !File.Exists(entry)) return false;
        var command = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-I", "-B", entry! }) command.ArgumentList.Add(arg);
        using var process = Process.Start(command) ?? throw new IOException("無法啟動 Hermes 記憶寫入。");
        // Drain both streams before waiting, even if the runtime emits diagnostics.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { content, old_text = oldText }));
        process.StandardInput.Close();
        // Once a write starts, await its receipt rather than abandon an uncertain mutation.
        await process.WaitForExitAsync();
        await errors;
        if (process.ExitCode != 0) return false;
        try
        {
            using var result = JsonDocument.Parse(await output);
            return result.RootElement.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public async Task<bool> SteerAsync(string runPath, string text, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, runPath + "/steer", new { input = text });
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        CheckResponse(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("accepted", out var value) && value.ValueKind == JsonValueKind.True;
    }

    private async Task<string?> ReadRunAsync(string runPath, Func<HermesUpdate, Task> onUpdate, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Get, runPath + "/events");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            CheckResponse(response);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is string line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                }
                if (line.Length != 0 || data.Length == 0) continue;

                string payload = data.ToString();
                data.Clear();
                if (payload == "[DONE]") return null;
                using var doc = JsonDocument.Parse(payload);
                var item = doc.RootElement;
                string kind = GetString(item, "event");
                if (kind == "run.completed") return GetString(item, "output");
                if (kind is "run.failed" or "run.cancelled" or "run.interrupted") throw RunFailure(kind);
                await onUpdate(ToUpdate(item, kind, runPath));
                ct.ThrowIfCancellationRequested();
            }
            return null;
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException ex) when (ex is not HermesRunException && !ct.IsCancellationRequested) { return null; }
    }

    private async Task<string> PollRunAsync(string runPath, Func<HermesUpdate, Task> onUpdate, CancellationToken ct)
    {
        await onUpdate(new("run.reconnecting"));
        while (true)
        {
            using var request = Request(HttpMethod.Get, runPath);
            using var response = await _http.SendAsync(request, ct);
            CheckResponse(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var item = doc.RootElement;
            string status = GetString(item, "status");
            if (status == "completed") return GetString(item, "output");
            if (status is "failed" or "cancelled" or "interrupted" or "unknown") throw RunFailure("run." + status);
            if (item.TryGetProperty("pending_controls", out var controls) && controls.ValueKind == JsonValueKind.Array)
            {
                foreach (var control in controls.EnumerateArray())
                {
                    string kind = GetString(control, "kind");
                    if (kind is "approval" or "clarify") await onUpdate(ToUpdate(control, kind + ".request", runPath));
                }
            }
            else if (status == "waiting_for_approval" && item.TryGetProperty("approval", out var approval))
                await onUpdate(ToUpdate(approval, "approval.request", runPath));
            await Task.Delay(800, ct);
        }
    }

    private static HermesUpdate ToUpdate(JsonElement item, string kind, string runPath)
    {
        string tool = GetString(item, "tool");
        if (kind is "approval.request" or "clarify.request")
        {
            string requestId = GetString(item, "prompt_id");
            if (requestId.Length == 0) requestId = GetString(item, "request_id");
            int? generation = item.TryGetProperty("execution_generation", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : null;
            string text = kind == "approval.request"
                ? ApprovalPrompt.Describe(GetString(item, "command"), GetString(item, "description"))
                : string.Join("\n", new[] { "question", "text" }.Select(field => GetString(item, field)).Where(s => s.Length > 0).Distinct());
            bool Allows(string choice) => item.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == choice);
            return new(kind, Control: new(runPath, kind, requestId, generation, text, Allows("session"), Allows("always")));
        }
        if (kind == "tool.completed" && item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
            kind = "tool.failed";
        return new(kind, GetString(item, kind == "message.delta" ? "delta" : "text"), tool);
    }

    public async Task RespondAsync(HermesControl control, string answer, CancellationToken ct)
    {
        bool clarify = control.Kind == "clarify.request";
        if (clarify && (control.Generation is null || control.RequestId.Length == 0))
            throw new InvalidOperationException("目前 Hermes 版本未提供這筆補充問題的回覆識別資訊。");
        if (!clarify && !(answer is "once" or "deny" || answer == "session" && control.AllowSession || answer == "always" && control.AllowAlways))
            throw new ArgumentException("請選擇這項詢問提供的授權選項。");

        var body = new Dictionary<string, object> { [clarify ? "answer" : "choice"] = answer };
        if (control.RequestId.Length > 0) body["request_id"] = control.RequestId;
        if (control.Generation is int generation) body["execution_generation"] = generation;
        using var request = Request(HttpMethod.Post, control.RunId + (clarify ? "/clarify" : "/approval"), body);
        using var response = await _http.SendAsync(request, ct);
        CheckResponse(response);
    }

    public async Task<string> ConnectionStatusAsync()
    {
        using var request = Request(HttpMethod.Get, "v1/toolsets");
        using var response = await _http.SendAsync(request);
        CheckResponse(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var enabled = doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => item.TryGetProperty("enabled", out var value) && value.ValueKind == JsonValueKind.True)
            .Select(item => GetString(item, "name"));
        return "本機 API 已連線 · " + string.Join("、", enabled);
    }

    public async Task ClearMemoryAsync()
    {
        // Delete the selected session explicitly and drain the dedicated profile's API sessions.
        if (_sessionId is not null) await DeleteSessionAsync(_sessionId);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            using var request = Request(HttpMethod.Get, "api/sessions?source=api_server&include_children=true&limit=100");
            using var response = await _http.SendAsync(request);
            CheckResponse(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(item => GetString(item, "id")).ToArray();
            if (ids.Length == 0) break;
            foreach (var id in ids)
            {
                if (!removed.Add(id)) throw new IOException("Hermes 仍保有已送出清除要求的會話。");
                await DeleteSessionAsync(id);
            }
        }
        await _resetBuiltInMemory();
        _conversation.Clear();
        File.Delete(_sessionFile);
        _sessionId = null;
        History = Array.Empty<ConversationTurn>();
        MemoryWarning = null;
    }

    private static async Task ResetBuiltInMemoryAsync()
    {
        string? executable = Environment.GetEnvironmentVariable("MIA_HERMES_EXECUTABLE");
        string? entry = Environment.GetEnvironmentVariable("MIA_HERMES_ENTRY");
        if (!File.Exists(executable) || !File.Exists(entry))
            throw new InvalidOperationException("找不到 Hermes 執行程式，請透過米亞啟動捷徑重新開啟。");
        var command = new ProcessStartInfo(executable!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-I", "-B", entry!, "mia-reset-memory" }) command.ArgumentList.Add(argument);
        using var process = Process.Start(command) ?? throw new IOException("無法啟動 Hermes 記憶清除。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Hermes 未能清除內建記憶，請檢查工作目錄中的設定。");
    }

    private async Task DeleteSessionAsync(string id)
    {
        if (id.Length == 0) throw new IOException("Hermes 回傳無效的會話識別碼。");
        using var request = Request(HttpMethod.Delete, "api/sessions/" + Uri.EscapeDataString(id));
        request.Headers.Add("X-MIA-Memory-Reset", "1");
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new IOException("Hermes 還有執行中或待處理的工作。請先停止或處理這些工作，再清除記憶。");
        if (response.StatusCode != HttpStatusCode.NotFound) CheckResponse(response);
    }

    private void SaveSession(string sessionId)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(_sessionFile, sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MemoryWarning = "米亞無法在重啟後接續這個工作對話。"; }
    }

    private async Task<bool> RequestStopAsync(string runPath)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = Request(HttpMethod.Post, runPath + "/stop", new { });
            using var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Hermes 拒絕本機連線，請由 3.2 啟動程式重新啟動。",
            HttpStatusCode.NotFound => "Hermes 未提供所需的工作 API，或這筆工作已超過保留期限。",
            HttpStatusCode.Conflict => "Hermes 的工作或確認狀態已變更，請查看目前的工作進度。",
            HttpStatusCode.TooManyRequests => "Hermes 已達同時工作的上限，請稍後再試。",
            _ => $"Hermes 本機 API 回傳 HTTP {(int)response.StatusCode}，請查看 Gateway 診斷。"
        });
    }

    private static HermesRunException RunFailure(string status) => new(status switch
    {
        "run.cancelled" => "Hermes 已取消工作。",
        "run.interrupted" => "Hermes Gateway 中斷了這項工作。",
        "run.unknown" => "Hermes 無法確認先前工作的結果，請先查看進度再決定是否重做。",
        _ => "Hermes 工作未完成，請檢查模型登入及 Gateway 診斷後再試。"
    });

    private static string GetString(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
    private sealed class HermesRunException(string message) : IOException(message);
}
