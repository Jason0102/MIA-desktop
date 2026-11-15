using System.Text.Json;

namespace Mia.Services;

internal static class TaskContinuation
{
    internal static string SessionId(string id) => Guid.TryParseExact(id, "N", out _) ? id :
        id.StartsWith("job-", StringComparison.Ordinal) && Guid.TryParseExact(id[4..], "N", out _) ? id[4..] :
        throw new InvalidOperationException("原任務識別資訊不完整，無法接續。");

    internal static string Context(TaskResult task) => JsonSerializer.Serialize(new
    {
        task.Title, task.Request, task.Reply, task.Files,
        Note = "修改成果時從 Files 的 jobs/<job_id>/output 路徑取得原 Codex 工作編號，使用 resume_job 接續原目錄。"
    });
}
