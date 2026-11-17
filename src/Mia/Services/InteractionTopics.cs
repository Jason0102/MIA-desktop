using System.Text.RegularExpressions;

namespace Mia.Services;

internal static class InteractionTopics
{
    private static readonly (string Name, string Pattern)[] Categories =
    [
        ("生活日常", "今天|昨天|最近|心情|生活|起床|睡覺"),
        ("飲食", "午餐|晚餐|早餐|咖啡|飲料|食物|餐廳|美食"),
        ("電影與戲劇", "電影|影集|電視劇|動畫片|劇情"),
        ("音樂", "音樂|歌曲|歌手|演唱會|樂團"),
        ("閱讀", "閱讀|小說|讀書|書籍|漫畫"),
        ("遊戲", "遊戲|電玩|桌遊|玩球"),
        ("旅行", "旅行|旅遊|度假|出國"),
        ("科技", "科技|人工智慧|模型|程式|電腦"),
        ("寵物", "桌寵|貓咪|狗狗|寵物"),
        ("天氣", "天氣|雨天|下雨|晴天|颱風")
    ];

    public static string[] For(string text)
    {
        string[] topics = Categories.Where(topic => Regex.IsMatch(text, topic.Pattern)).Select(topic => topic.Name).ToArray();
        return topics.Length == 0 ? ["其他閒聊"] : topics;
    }

    public static bool IsCategory(string name) => name == "其他閒聊" || Categories.Any(category => category.Name == name);

    public static bool LooksLikeTask(string text) => Regex.IsMatch(text,
        "請(?:你|幫|先|將|把|查|寫|列|做|執行)|幫我|替我|可否(?:查|寫|做)|(?:開啟|生成|建立|整理|搜尋|查詢|設計|實作|推送|更新|修改|上傳|下載).+");

    public static bool HasChatLead(string text) =>
        Regex.IsMatch(text, @"^(?:(?:米亞|MIA)[，！、,:!\s]+)?(?:你好|早安|午安|晚安|謝謝|嗨|哈囉|今天[我你])[^。！？!?\n]{0,80}[。！？!?\n]", RegexOptions.IgnoreCase);

    public static string? Feedback(string text)
    {
        string normalized = text.Trim().TrimEnd('。', '！', '!', ' ', '～');
        if (Regex.IsMatch(normalized, "^(?:我(?:很)?喜歡這樣|這樣很好|做得好|做得很棒|這個語氣我喜歡)$")) return "positive";
        if (Regex.IsMatch(normalized, "^(?:請安靜|請安靜一下|現在不要打擾我|不用再邀請我|不要再問了)$")) return "quiet";
        if (Regex.IsMatch(normalized, "^(?:不是這樣|請換個語氣|請修正這個回答)$")) return "correction";
        return null;
    }
}
