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
        using var handler = new TestHandler(async (request, ct) =>
        {
            check(request.Headers.Authorization?.Parameter == "fake-token", "Hermes HTTP uses the local bearer token");
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/runs")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
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
                    data: {"event":"approval.request","prompt_id":"check-approval","execution_generation":3,"description":"Review requested operation"}

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
                    && root.GetProperty("choice").GetString() == "once", "Hermes controls use the exact request and run generation");
                return Json("{}");
            }
            if (path == "/v1/runs/run_check/stop")
            {
                stops++;
                return Json("{\"status\":\"stopping\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new HermesService(local, "fake-token", handler);
        string output = await service.ReplyAsync("Make a local report", Array.Empty<byte[]>(), CancellationToken.None, async update =>
        {
            updates.Add(update);
            if (update.Control is { } control) await service.RespondAsync(control, "once", CancellationToken.None);
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
        await service.ClearConversationAsync();
        check(!File.Exists(Path.Combine(SettingsStore.DataDirectory, "hermes-conversation.jsonl")), "Clearing Hermes history also removes the dedicated local transcript");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Stream(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/event-stream") };
    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
