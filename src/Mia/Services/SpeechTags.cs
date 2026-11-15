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
        音量與表達方式依場景調整：安慰用 softly，說悄悄話用 whispers，需要呼喊才用 shouts；角色或口音僅在使用者要求或角色演出需要時使用。節奏依台詞選擇：認真說明可放慢，興奮時可加快，戲劇轉折或思考時才加 pause 或 long pause。
        人物反應須符合當下動作：真覺得好笑用 laughs，鬆一口氣或無奈用 sighs，突遇意外用 gasps；清喉嚨、哭泣等反應只在場景需要時使用。音效只在使用者要求或明確的故事演出需要時使用。
        情緒標籤同時驅動語音與角色動畫，關閉語音時仍須提供。每句對使用者說的台詞都必須在句首明確標記情緒，不能只靠文字或標點暗示。平實解說使用 thoughtful 或 peaceful。自由描述或複合指示請包含最貼近的情緒英文詞，例如 [audio: sad, softly] 或 [audio: excited, speaking quickly]，讓角色能辨識表情。標籤放在受影響的台詞前，語氣轉變時可在句中加入；語音逐句生成，跨句仍需維持相同語氣時在新句開頭重加。避免堆疊互相衝突的指示。程式碼、工具參數、路徑與來源不加標籤；JSON 回應僅在給使用者的 message 字串內加標籤。
        例：[audio: proud]你真的做到了！[audio: curious]哪一步最有成就感？
        """;
    private static readonly string[] Names = { "happy", "excited", "sad", "curious", "whispers", "laughs", "sighs", "angry" };
    private const string Prefix = "audio:";
    private static readonly Regex AudioTag = new(@"\[audio:[ \t]*(?<cue>[^\[\]\r\n]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"\[(?:audio:[^\[\]\r\n]*|happy|excited|sad|curious|whispers|laughs|sighs|angry)\][ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    public static string ForElevenLabs(string text) => AudioTag.Replace(text, match => "[" + match.Groups["cue"].Value.Trim() + "]");

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
                    || Names.Any(n => n.StartsWith(tail, StringComparison.OrdinalIgnoreCase))))
                    text = text[..start]; // A tag may span several SSE events.
            }
        }
        return Tags.Replace(text, "");
    }

    public static bool HasWords(string text) => !string.IsNullOrWhiteSpace(Display(text));
}
