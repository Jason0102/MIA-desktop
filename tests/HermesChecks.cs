using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mia.Services;

internal static class HermesChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var prefixMethod = typeof(HermesService).Assembly.GetType("Mia.Services.ReplyStream")!
            .GetMethod("MessagePrefix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string Prefix(string value) => (string)prefixMethod.Invoke(null, new object[] { value })!;
        string content = "[audio: happy]你好！\n路徑 C:\\test 與 \"引號\" 🌟";
        string json = JsonSerializer.Serialize(new { message = content, action = "chat", task = "INTERNAL" });
        bool partial = false;
        for (int i = 0; i <= json.Length; i++)
        {
            string decoded = Prefix(json[..i]);
            check(content.StartsWith(decoded, StringComparison.Ordinal), "partial routing JSON never exposes escapes or internal fields");
            partial |= decoded.Length > 0 && decoded.Length < content.Length;
        }
        check(partial && Prefix(json) == content, "router message streams before full JSON and decodes Unicode safely");
        check(Prefix("{\"task\":\"secret\",\"message\":\"hello") == "hello", "routing task before message remains private");
        Uri local = new("http://127.0.0.1:8642");
        try
        {
            using var remote = new HermesService(new Uri("http://localhost.example:8642"), "fake-token");
            check(false, "Hermes refuses a non-loopback destination");
        }
        catch (ArgumentException) { check(true, "Hermes refuses a non-loopback destination"); }

        var updates = new List<HermesUpdate>();
        string? session = null;
        int stops = 0;
        int memoryResets = 0;
        bool sessionDeleted = false;
        using var handler = new TestHandler(async (request, ct) =>
        {
            check(request.Headers.Authorization?.Parameter == "fake-token", "Hermes HTTP uses the local bearer token");
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sessions/" + session && request.Method == HttpMethod.Delete)
            {
                check(request.Headers.TryGetValues("X-MIA-Memory-Reset", out var resetHeaders) &&
                    resetHeaders.SequenceEqual(new[] { "1" }), "memory reset explicitly permits recovery of a lost run");
                sessionDeleted = true;
                return Json("{}");
            }
            if (path == "/api/sessions") return Json("{\"data\":[]}");
            if (path == "/v1/runs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                check(body.RootElement.GetProperty("model_options").GetProperty("reasoning_effort").GetString() == "medium", "Hermes sends explicit per-request reasoning effort");
                check(body.RootElement.GetProperty("instructions").GetString()!.Contains(SpeechTags.Instructions), "Hermes receives speech and EMO instructions");
                string? sessionId = body.RootElement.GetProperty("session_id").GetString();
                session ??= sessionId;
                check(sessionId == session && request.Headers.Contains("Idempotency-Key"), "Hermes keeps its conversation and scopes admissions by idempotency key");
                return Json("{\"run_id\":\"run_check\"}");
            }
            if (path == "/v1/runs/run_check/events")
                return Stream("""
                    : keepalive

                    id: 1
                    data: {"event":"tool.started","tool":"mia_codex_run"}

                    id: 2
                    data: {"event":"approval.request","prompt_id":"check-approval","execution_generation":3,"description":"Allow computer_use to perform `type`?","command":"computer_use: type 'Sticky note' [FOREGROUND — briefly raises the window / changes focus]","choices":["once","session","always","deny"]}

                    id: 3
                    data: {"event":"message.delta","delta":"Progress only"}

                    id: 4
                    data: {"event":"tool.completed","tool":"mia_codex_run"}


                    """);
            if (path == "/v1/runs/run_check") return Json("{\"status\":\"completed\",\"output\":\"Codex returned the final result\"}");
            if (path == "/v1/runs/run_check/approval")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement;
                check(root.GetProperty("request_id").GetString() == "check-approval" && root.GetProperty("execution_generation").GetInt32() == 3
                    && root.GetProperty("choice").GetString() == "session", "Hermes controls use the selected scope and exact request and run generation");
                return Json("{}");
            }
            if (path == "/v1/runs/run_check/stop")
            {
                stops++;
                return Json("{\"status\":\"stopping\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new HermesService(local, "fake-token", handler,
            () => { memoryResets++; return Task.CompletedTask; });
        string output = await service.ReplyAsync("Make a local report", Array.Empty<byte[]>(), CancellationToken.None, async update =>
        {
            updates.Add(update);
            if (update.Control is { } control)
            {
                check(control.AllowSession && control.AllowAlways &&
                    control.Text == "我想把視窗切到前面，再在視窗裡輸入文字，可以嗎？",
                    "Hermes approval preserves available scopes and describes the desktop action without raw arguments");
                await service.RespondAsync(control, "session", CancellationToken.None);
            }
        });
        check(output == "Codex returned the final result" && updates.Count(update => update.Tool == "mia_codex_run") == 2,
            "Hermes surfaces Codex tool events and recovers the canonical result after stream disconnect");
        using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try
            {
                await service.ReplyAsync("Stop after tool start", Array.Empty<byte[]>(), cancellation.Token, update =>
                {
                    if (update.Event == "tool.started") cancellation.Cancel();
                    return Task.CompletedTask;
                });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            check(cancelled && stops == 1, "Cancelling a desktop request sends a real stop to the accepted Hermes run");
        }
        await service.ClearMemoryAsync();
        check(sessionDeleted && memoryResets == 1 && !File.Exists(Path.Combine(SettingsStore.DataDirectory, "hermes-conversation.jsonl")),
            "Clearing Hermes memory deletes the server session, resets built-in memory, and removes the local transcript");
        var sessionIds = new HashSet<string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var parallelHandler = new TestHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/runs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string id = body.RootElement.GetProperty("session_id").GetString()!;
                lock (sessionIds)
                {
                    sessionIds.Add(id);
                    if (sessionIds.Count == 2) bothStarted.TrySetResult();
                }
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                return Json(JsonSerializer.Serialize(new { run_id = id }));
            }
            return Stream("data: {\"event\":\"run.completed\",\"output\":\"Parallel result\"}\n\n");
        });
        using var parallel = new HermesService(local, "fake-token", parallelHandler);
        var results = await Task.WhenAll(
            parallel.ReplyAsync("First", Array.Empty<byte[]>(), CancellationToken.None, _ => Task.CompletedTask, true),
            parallel.ReplyAsync("Second", Array.Empty<byte[]>(), CancellationToken.None, _ => Task.CompletedTask, true));
        check(sessionIds.Count == 2 && results.All(result => result == "Parallel result"),
            "Independent tasks enter Hermes concurrently with distinct sessions and both results return");
        check(new ConversationStore("hermes-conversation.jsonl").LoadRecent().Count == 2,
            "Concurrent results both persist in the local transcript");
        string taskSession = Guid.NewGuid().ToString("N");
        var resumed = new List<string>();
        bool steered = false;
        using var resumeHandler = new TestHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/steer"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                steered = body.RootElement.GetProperty("input").GetString() == "Change the report";
                return Json("{\"accepted\":true}");
            }
            if (request.RequestUri.AbsolutePath == "/v1/runs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                resumed.Add(body.RootElement.GetProperty("session_id").GetString()!);
                return Json("{\"run_id\":\"resume_run\"}");
            }
            return Stream("data: {\"event\":\"run.completed\",\"output\":\"Done\"}\n\n");
        });
        using var resume = new HermesService(local, "fake-token", resumeHandler);
        string? acceptedPath = null;
        await resume.ReplyAsync("Create", Array.Empty<byte[]>(), CancellationToken.None,
            update => { if (update.Event == "run.accepted") acceptedPath = update.Text; return Task.CompletedTask; }, taskSession: taskSession);
        await resume.ReplyAsync("Revise", Array.Empty<byte[]>(), CancellationToken.None,
            _ => Task.CompletedTask, taskSession: taskSession);
        check(resumed.SequenceEqual(new[] { taskSession, taskSession }), "Task supplements retain the task's Hermes conversation");
        check(await resume.SteerAsync(acceptedPath!, "Change the report", CancellationToken.None) && steered,
            "Live supplements reach the accepted Hermes run through the steer API");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Stream(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/event-stream") };
    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
