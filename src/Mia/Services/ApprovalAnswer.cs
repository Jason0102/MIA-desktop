namespace Mia.Services;

internal static class ApprovalAnswer
{
    internal static string? Parse(string text, string title)
    {
        string value = text.Trim();
        foreach (string prefix in new[] { "「" + title + "」", title })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) { value = value[prefix.Length..].TrimStart(' ', '：', ':', '，', ','); break; }
        return value.TrimEnd('。', '！', '!', '.', ' ') switch
        {
            "允許這一次" or "允許" or "同意" or "核准" => "once",
            "拒絕" or "不允許" or "不同意" or "不要執行" => "deny",
            _ => null
        };
    }
}
