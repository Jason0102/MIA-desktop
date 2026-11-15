using System.Reflection;
using System.Text.Json;
using Mia.Services;

internal static class MemoryContinuityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var assembly = typeof(HermesService).Assembly;
        var approval = assembly.GetType("Mia.Services.ApprovalAnswer")!.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Parse(string text) => (string?)approval.Invoke(null, [text, "報告任務"]);
        check(Parse("允許這一次") == "once" && Parse("「報告任務」：同意。") == "once", "explicit approval binds to a task");
        check(Parse("不同意") == "deny" && Parse("不要執行") == "deny", "negative approval stays denied");
        check(Parse("如果安全就同意") is null && Parse("允許，但先別執行") is null && Parse("好的") is null, "ambiguous and conditional replies never grant approval");
        var display = assembly.GetType("Mia.Services.TaskCompletion")!.GetMethod("Display", BindingFlags.NonPublic | BindingFlags.Static)!;
        string raw = "[audio: happy]已完成報告。\n工作編號：" + new string('a', 32) +
            "\n[報告](C:/Users/Test/jobs/" + new string('a', 32) + "/output/報告.docx)\n注意：第二章資料仍待確認。";
        string clean = (string)display.Invoke(null, [raw, true])!;
        check(!clean.Contains("C:/") && !clean.Contains(new string('a', 32)) && !clean.Contains("工作編號"), "completion chat hides paths and job IDs");
        check(clean.Contains("已完成報告") && clean.Contains("仍待確認"), "completion preserves summary and limitations");
        check(((string)display.Invoke(null, ["C:\\test\\output.txt", true])!).Contains("檔案連結"), "file-only completion has a natural fallback");
        var resultType = assembly.GetType("Mia.Services.TaskResult")!;
        string id = new string('b', 32);
        var result = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Id = id, Title = "歷史任務", Request = "原委託", Reply = raw,
            UpdatedAt = DateTimeOffset.Now, Files = new[] { "jobs/" + new string('a', 32) + "/output/報告.docx" } }), resultType)!;
        var continuation = assembly.GetType("Mia.Services.TaskContinuation")!;
        string context = (string)continuation.GetMethod("Context", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [result])!;
        using var parsed = JsonDocument.Parse(context);
        check(parsed.RootElement.GetProperty("Reply").GetString() == raw && parsed.RootElement.GetProperty("Request").GetString() == "原委託", "archived task carries full original context independent of recent cache");
        var session = continuation.GetMethod("SessionId", BindingFlags.NonPublic | BindingFlags.Static)!;
        check((string)session.Invoke(null, [id])! == id, "archived continuation reuses original Hermes session");
        check((string)session.Invoke(null, ["job-" + id])! == id, "legacy delivery gets stable session with restored snapshot");
    }
}
