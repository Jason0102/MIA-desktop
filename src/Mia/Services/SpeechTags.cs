using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mia.Services;

// Preserve speech directions in history/replay while hiding them in chat.
public static class SpeechTags
{
    public const string Instructions = """
        語音表演格式：用 [audio: 英文指示] 標記需要改變表演的地方。程式會轉成 ElevenLabs 的 [英文指示]。
        可用 Eleven v4 支援的任何標籤，包括自訂的簡短英文描述與組合，例如 [audio: whispering, playful]。以下是選擇條件與例子，不是限制清單。
        情緒取決於米亞此刻的反應：分享好消息用 happy 或 excited，慶祝成就用 proud，開玩笑用 playful，驚喜用 amazed 或 startled，真心追問用 curious，斟酌回答用 thoughtful，安心陪伴用 peaceful，感同身受用 sad 或 sympathetic。疲憊、失落、困惑、緊張、恐懼或憤怒等語氣用於上下文確實呈現這些情緒時；避免對求助者嘲諷或發怒。
        用獨立的 [delivery: 英文指示] 標記語速、停頓與說話力度。可用 slowly（慢）、rushed（急促）、pause（停頓）、long pause（較長停頓）、softly（柔聲）、whispers（耳語）、shouts（呼喊）。每類只選一個，無須每句都加。語速與力度放在句首情緒標籤後；停頓放在應停頓的位置。依心情與當下情境選擇，音訊標籤的效果隨聲線與上下文而異。
        人物反應須符合當下動作：真覺得好笑用 laughs，鬆一口氣或無奈用 sighs，突遇意外用 gasps；清喉嚨、哭泣等反應只在場景需要時使用。音效只在使用者要求或明確的故事演出需要時使用。
        情緒標籤同時驅動語音與角色動畫，關閉語音時仍須提供。每句台詞在句首標記情緒，平實解說使用 thoughtful 或 peaceful。語氣轉變時可在句中加入情緒標籤。語音逐句生成，跨句要維持相同語氣時在新句開頭重加。避免堆疊互相衝突的指示。程式碼、工具參數、路徑與來源不加標籤。JSON 回應只在給使用者的 message 字串內加標籤。
        例：[audio: proud]你真的做到了！[audio: thoughtful][delivery: slowly, softly]我們先回顧一下，[delivery: pause]哪一步最有成就感？
        """;
    private static readonly string[] Names = { "happy", "excited", "sad", "curious", "whispers", "laughs", "sighs", "angry" };
    private const string Prefix = "audio:";
    private const string DeliveryPrefix = "delivery:";
    private static readonly Regex AudioTag = new(@"\[audio:[ \t]*(?<cue>[^\[\]\r\n]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeliveryTag = new(@"\[delivery:[ \t]*(?<cue>[^\[\]\r\n]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"\[(?:audio:[^\[\]\r\n]*|happy|excited|sad|curious|whispers|laughs|sighs|angry)\][ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[][] DeliveryCues = [["slowly", "rushed"], ["pause", "long pause"], ["softly", "whispers", "shouts"]];

    private static IEnumerable<string> Directions(Match match)
    {
        var requested = match.Groups["cue"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string[] allowed in DeliveryCues)
            if (requested.FirstOrDefault(cue => allowed.Contains(cue, StringComparer.OrdinalIgnoreCase)) is { } direction)
                yield return direction.ToLowerInvariant();
    }

    public static IReadOnlyList<string> DeliveryDirections(string text) => DeliveryTag.Matches(text)
        .SelectMany(Directions).Distinct(StringComparer.Ordinal).ToArray();

    internal static string WithoutDelivery(string text) => DeliveryTag.Replace(text, "");

    public static bool RequestsUrls(string request)
    {
        const string target = @"(?:網址|連結|链接|超連結|URL|URLs|links?|web addresses?)";
        const string action = @"(?:念|唸|朗讀|读出|讀出|read|say|speak)";
        if (Regex.IsMatch(request, @"(?:不要|別|不必|不用|避免|do not|don't|never)[^。！？\n]{0,24}" + action, RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(request, action + @".{0,30}" + target + "|" + target + @".{0,30}" + action, RegexOptions.IgnoreCase);
    }

    public static string SpokenText(string text, bool readUrls = false)
    {
        // Keep display text and audio directions intact outside this speech-only transform.
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^\r\n]*?\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^\r\n]*?)\)", m => readUrls ? m.Groups[1].Value + " " + m.Groups[2].Value : m.Groups[1].Value);
        if (!readUrls)
            text = Regex.Replace(text, @"(?i)(?:https?://|www\.)[^\s<>\u3000，。！？；）]+|\b(?:[a-z0-9-]+\.)+(?:com|org|net|edu|gov|io|ai|tw)(?:/[^\s<>，。！？；）]*)?", "");
        text = Regex.Replace(text, @"(?m)^\s*(?:#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)", "");
        text = Regex.Replace(text, @"(?m)^\s*[-=_]{3,}\s*$", "");
        text = Regex.Replace(text, @"```[^\r\n]*", "");
        text = Regex.Replace(text, @"[*_`|~]", "");
        text = Regex.Replace(text, @"<[^>]+>|【[^】]*】", "");
        return ForElevenLabs(text.Trim());
    }
    public static string ForElevenLabs(string text) => DeliveryTag.Replace(
        AudioTag.Replace(text, match => "[" + match.Groups["cue"].Value.Trim() + "]"),
        match => string.Concat(Directions(match).Select(cue => "[" + cue + "]")));

    public static IEnumerable<string> Segments(string text)
    {
        int start = 0;
        foreach (Match tag in Tags.Matches(text))
        {
            if (HasWords(text[start..tag.Index]))
            {
                yield return text[start..tag.Index];
                start = tag.Index;
            }
        }
        if (HasWords(text[start..])) yield return text[start..];
    }

    public static string Display(string text, bool streaming = false)
    {
        if (streaming)
        {
            int start = text.LastIndexOf('[');
            if (start >= 0)
            {
                string tail = text[(start + 1)..];
                if (!tail.Contains(']') && (Prefix.StartsWith(tail, StringComparison.OrdinalIgnoreCase)
                    || tail.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    || DeliveryPrefix.StartsWith(tail, StringComparison.OrdinalIgnoreCase)
                    || tail.StartsWith(DeliveryPrefix, StringComparison.OrdinalIgnoreCase)
                    || Names.Any(n => n.StartsWith(tail, StringComparison.OrdinalIgnoreCase))))
                    text = text[..start]; // A tag may span several SSE events.
            }
        }
        return Tags.Replace(WithoutDelivery(text), "");
    }

    public static bool HasWords(string text) => !string.IsNullOrWhiteSpace(Display(text));
}
