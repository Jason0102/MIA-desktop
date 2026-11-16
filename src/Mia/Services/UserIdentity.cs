using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mia.Services;

public static class UserIdentity
{
    public sealed record Person(string Id, string DisplayName, string[] Aliases);
    public sealed record Binding(string Provider, string Subject, string UserId);
    public sealed record Directory(Person[] Users, Binding[] Bindings);

    // Provider and subject must come from the transport, never from message text.
    public static string Context(string provider, string subject)
    {
        string local = Path.Combine(SettingsStore.DataDirectory, "users.json");
        string path = File.Exists(local) ? local : Path.Combine(AppContext.BaseDirectory, "users.json");
        try
        {
            var directory = JsonSerializer.Deserialize<Directory>(File.ReadAllText(path));
            var bindings = directory?.Bindings.Where(b => b.Provider == provider && b.Subject == subject).ToArray();
            if (bindings?.Length != 1) return "目前來源尚未綁定使用者身分，請勿推定為本機使用者。";
            var people = directory!.Users.Where(p => p.Id == bindings[0].UserId).ToArray();
            if (people.Length != 1) return "目前使用者身分設定不完整。";
            return "目前互動使用者資料：" + JsonSerializer.Serialize(people[0]) +
                "。來源：" + provider + "。此身分僅適用於本次來源綁定；其他來源須獨立辨識，不能因同名就合併。身分資訊不代表額外操作授權。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        {
            return "目前使用者身分設定無法讀取，請勿猜測使用者身分。";
        }
    }
}
