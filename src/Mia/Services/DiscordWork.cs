using System.IO;
using System.Text.Json;

namespace Mia.Services;

public sealed record DiscordWorkItem(string Id, string UserId, string DiscordUserId, string ChannelId,
    string Task, string Request, string Reply, string[] Files, string Status, DateTimeOffset UpdatedAt)
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
