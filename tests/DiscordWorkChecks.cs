using System.Text.Json;
using System.IO;
using Mia.Services;

internal static class DiscordWorkChecks
{
    internal static void Run(string scratch, Action<bool, string> check)
    {
        string state = Path.Combine(scratch, "discord-state");
        Directory.CreateDirectory(Path.Combine(state, "discord-work"));
        string registry = Path.Combine(state, "users.json");
        File.WriteAllText(registry, JsonSerializer.Serialize(new UserIdentity.Directory(
            [new("jason", "Jason", []), new("other", "Other", [])],
            [new("desktop", "local", "jason"), new("discord", "123", "jason"), new("discord", "456", "other")])));
        var item = new DiscordWorkItem("a", "jason", "123", "channel", "task", "make PDF", "done", ["result.pdf"], "running", DateTimeOffset.UtcNow);
        string snapshot = Path.Combine(state, "discord-work", "a.json");
        check(DiscordWork.IsConversation(item with { Request = "收到，檔案正確，多謝囉", Files = [] }), "DC acknowledgement is hidden");
        check(DiscordWork.IsConversation(item with { Request = "另外你喜歡吃什麼當晚餐？", Files = [] }), "DC small talk is hidden");
        check(!DiscordWork.IsConversation(item with { Request = "你好，請幫我製作 PDF", Files = [] }), "DC mixed greeting and task is retained");
        check(!DiscordWork.IsConversation(item with { Request = "謝謝，這份附件", Files = ["a.pdf"] }), "DC artifacts remain visible");
        check(DiscordWork.IsConversation(item with { Request = "天氣讓人好想睡", Files = [], Kind = "chat" }), "DC semantic chat classification is hidden");
        check(!DiscordWork.IsConversation(item with { Request = "早安", Files = [], Kind = "task" }), "DC task classification takes precedence over legacy phrases");
        File.WriteAllText(snapshot, JsonSerializer.Serialize(item));
        File.WriteAllText(Path.Combine(state, "discord-work", "b.json"), JsonSerializer.Serialize(item with { Id = "b", UserId = "other", DiscordUserId = "456" }));
        File.WriteAllText(Path.Combine(state, "discord-work", "c.json"), "{partial");
        check(DiscordWork.Read(state) is [ { Active: true, UserId: "jason" } ], "DC work center excludes other identities and broken snapshots");
        File.WriteAllText(snapshot, JsonSerializer.Serialize(item with { Status = "completed" }));
        check(DiscordWork.Read(state) is [ { Active: false, Reply: "done", Files: ["result.pdf"] } ], "DC completed work keeps reply and attachments");
        File.WriteAllText(registry, "{}");
        check(DiscordWork.Read(state).Length == 0, "DC work center fails closed without identity binding");
    }
}
