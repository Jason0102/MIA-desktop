using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mia.Services;

public sealed record RoleProfile(string Instructions,string? Warning)
{
    private const string Fallback="你是米亞 Mia，活潑、好奇、溫暖的貓精靈桌面夥伴，使用自然的台灣繁體中文回應。";
    public static RoleProfile Load(string path)
    {
        try
        {
            if(new FileInfo(path).Length>128*1024)throw new InvalidDataException();
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            var root=doc.RootElement;
            string basic=root.GetProperty("basic").GetString()??"";
            var mia=root.GetProperty("mia");
            if(string.IsNullOrWhiteSpace(basic)||mia.ValueKind!=JsonValueKind.Object ||
                !mia.TryGetProperty("identity",out var identity)||string.IsNullOrWhiteSpace(identity.GetString()))throw new InvalidDataException();
            var text=new StringBuilder(basic);
            foreach(string name in new[]{"identity","appearance","mbti","kelly","16pf","prefer","story"})
            {
                if(!mia.TryGetProperty(name,out var value))continue;
                if(value.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))text.AppendLine().Append(value.GetString());
                else if(value.ValueKind==JsonValueKind.Array)
                    foreach(var item in value.EnumerateArray())
                        if(item.ValueKind==JsonValueKind.String)text.AppendLine().Append(item.GetString());
            }
            return new(text.ToString(),null);
        }
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        { return new(Fallback,"角色設定 role.json 無法讀取或格式不正確，暫用預設角色。修正後重新啟動米亞。"); }
    }
    public static string TimeContext(DateTimeOffset now,string timezone) =>
        $"本次發話的使用者本地時間：{now:yyyy-MM-dd HH:mm:ss zzz}，{now:dddd}，時區：{timezone}。請依此日期與時間理解今天、早晚與相對日期。這是時間資訊，不代表已看見或執行任何活動。";
}
