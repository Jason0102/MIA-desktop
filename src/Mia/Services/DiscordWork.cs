using System.IO;
using System.Text.Json;

namespace Mia.Services;

public sealed record DiscordWorkItem(string Id, string UserId, string DiscordUserId, string ChannelId,
    string Task, string Request, string Reply, string[] Files, string Status, DateTimeOffset UpdatedAt,
    string? Kind = null)
{
    public bool Active => Status is "queued" or "running";
    public string StatusText => Status switch
    {
        "queued" => "等待執行", "running" => "執行中", "completed" => "已完成",
        "cancelled" => "已停止", "interrupted" => "因重新啟動而中斷", _ => "執行或傳送失敗"
    };
}

public static class DiscordWork
{
    public static bool IsConversation(DiscordWorkItem item)
    {
        if (item.Files.Length > 0) return false;
        if (item.Kind == "chat") return true;
        if (item.Kind == "task") return false;
        string text = item.Request.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
            "請|幫我|製作|整理|查詢|搜尋|傳給我|給我.{0,30}(檔案|文件)|開啟|打開|修改|生成|產生|畫一|提醒|記住|安排|分析"))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            "^(你好|嗨|哈囉|早安|午安|晚安|謝謝|多謝|收到|聽到了|我過得|另外你喜歡|你喜歡|你今天|我要吃|但是健康餐)")
            || System.Text.RegularExpressions.Regex.IsMatch(text, "^(好|好的|了解|OK)[！!。～~ ]*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    public static DiscordWorkItem[] Read(string stateDirectory)
    {
        try
        {
            var people = JsonSerializer.Deserialize<UserIdentity.Directory>(File.ReadAllText(Path.Combine(stateDirectory, "users.json")));
            if (people?.Bindings is null || people.Users is null) return [];
            var local = people.Bindings.Where(b => b.Provider == "desktop" && b.Subject == "local").ToArray();
            if (local?.Length != 1) return [];
            string owner = local[0].UserId;
            string directory = Path.Combine(stateDirectory, "discord-work");
            if (!Directory.Exists(directory)) return [];
            var result = new List<DiscordWorkItem>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                    var item = JsonSerializer.Deserialize<DiscordWorkItem>(File.ReadAllText(path));
                    if (item is null || item.UserId != owner || item.Request is null || item.Reply is null || item.Files is null) continue;
                    if (IsConversation(item)) continue;
                    var bindings = people!.Bindings.Where(b => b.Provider == "discord" && b.Subject == item.DiscordUserId).ToArray();
                    if (bindings.Length == 1 && bindings[0].UserId == owner) result.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            return result.OrderByDescending(item => item.UpdatedAt).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return []; }
    }
}
