using System.IO;
using System.Text.Json;

namespace Mia.Services;

public sealed record MoodSnapshot(double Pleasantness, double Energy, double Affinity,
    double Playfulness, string State, IReadOnlyList<string> Reasons, DateTimeOffset UpdatedAt)
{
    public IReadOnlyDictionary<string, int> RecentTopics { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> Preferences { get; init; } = new Dictionary<string, string>();
    internal string? MemoryGeneration { get; init; }
}

public static class InteractionMood
{
    private sealed record Signal(string Label, double[] Effects, double DailyCap);
    private sealed record StateRule(int Dimension, double Threshold, string Label);
    private sealed record MoodRules(double[] Baseline, double RecentHalfLifeHours, double AffinityHalfLifeHours,
        int WindowDays, Dictionary<string, Signal> Signals, StateRule[] States, string DefaultState, string DefaultReason);

    private static readonly Lazy<MoodRules> Rules = new(() =>
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "interaction-mood.json"));
            var rules = JsonSerializer.Deserialize<MoodRules>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (rules?.Baseline is not { Length: 4 } || rules.Signals is null || rules.States is null ||
                rules.RecentHalfLifeHours <= 0 || rules.AffinityHalfLifeHours <= 0 || rules.WindowDays <= 0 ||
                rules.Signals.Values.Any(signal => signal?.Effects is not { Length: 4 }) ||
                rules.States.Any(rule => rule is null || rule.Dimension is < 0 or > 3))
                throw new InvalidDataException("Invalid interaction mood rules.");
            return rules;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new([50, 42, 30, 32], 18, 504, 90, [], [], "平穩", "無法讀取心情規則，維持角色基準"); }
    });

    public static MoodSnapshot Calculate(IEnumerable<InteractionEvent> events, DateTimeOffset now)
    {
        var rules = Rules.Value;
        var values = rules.Baseline.ToArray();
        var reasons = new Dictionary<string, double>();

        // A local day limits bursts. Decay uses elapsed time so restarts do not change the result.
        var history = events as IReadOnlyCollection<InteractionEvent> ?? events.ToArray();
        foreach (var day in history.Where(e => e.At <= now && now - e.At <= TimeSpan.FromDays(rules.WindowDays) &&
                     !string.IsNullOrEmpty(e.Kind) &&
                     rules.Signals.ContainsKey(e.Kind) && (e.Kind != "task_result" || e.Status is "completed" or "succeeded" or "success"))
                 .OrderByDescending(e => e.At)
                 .GroupBy(e => (e.Kind, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.At, TimeZoneInfo.Local).DateTime))))
        {
            var signal = rules.Signals[day.Key.Kind];
            double used = 0;
            foreach (var item in day)
            {
                if (used >= signal.DailyCap) break;
                double intensity = item.Kind == "ball_active" ? Math.Clamp(item.Seconds / 60, 0, 2) : 1;
                if (!double.IsFinite(intensity) || intensity <= 0) continue;
                double weight = Math.Min(signal.DailyCap - used, intensity / (1 + used));
                used += weight;
                double hours = (now - item.At).TotalHours;
                double recent = weight * Math.Pow(.5, hours / rules.RecentHalfLifeHours);
                double longTerm = weight * Math.Pow(.5, hours / rules.AffinityHalfLifeHours);
                for (int dimension = 0; dimension < values.Length; dimension++)
                    values[dimension] += signal.Effects[dimension] * (dimension == 2 ? longTerm : recent);
                reasons[signal.Label] = reasons.GetValueOrDefault(signal.Label) +
                    recent * (signal.Effects[0] + signal.Effects[1] + signal.Effects[3]) + longTerm * signal.Effects[2] * .3;
            }
        }

        string state = rules.States.FirstOrDefault(rule => values[rule.Dimension] >= rule.Threshold)?.Label ?? rules.DefaultState;
        var strongest = reasons.Where(r => r.Value >= .5).OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(3).Select(r => r.Key).ToArray();
        var topics = history.Where(item => item.Kind == "chat" && item.At <= now && now - item.At <= TimeSpan.FromDays(30))
            .SelectMany(item => item.Topics ?? []).Where(topic => !string.IsNullOrEmpty(topic)).GroupBy(topic => topic)
            .ToDictionary(group => group.Key, group => group.Count());
        return new(Bound(values[0]), Bound(values[1]), Bound(values[2]), Bound(values[3]), state,
            strongest.Length > 0 ? strongest : [rules.DefaultReason], now) { RecentTopics = topics };
    }

    private static double Bound(double value) => Math.Round(Math.Clamp(value, 0, 100), 1);

    public static MoodSnapshot Desktop() => FromMemory(InteractionMemory.Desktop());

    internal static MoodSnapshot FromMemory(InteractionMemory? memory)
    {
        string? generation = memory?.Generation();
        return Calculate(memory?.Read() ?? [], DateTimeOffset.Now) with
        {
            MemoryGeneration = generation,
            Preferences = memory?.GetPreferences() ?? new Dictionary<string, string>()
        };
    }

    internal static string Prompt(MoodSnapshot mood, string request) => FormattableString.Invariant($"""
        米亞的互動心情快照（0–100）：{mood.State}。愉悅度 {mood.Pleasantness:F0}、活力 {mood.Energy:F0}、親近感 {mood.Affinity:F0}、玩心 {mood.Playfulness:F0}。
        最近影響心情的互動：{string.Join('、', mood.Reasons)}。這些是角色狀態，不代表使用者的情緒或同意。
        依當句內容決定情緒。嚴肅、求助或需要清楚說明時，即使米亞本來活潑，也要優先使用關切、平穩的表達。
        日常閒聊時，活力及玩心較高可讓語氣更輕快；平穩或安靜陪伴可用 softly，解釋較難的步驟可用 slowly，話題轉折可加 pause。力度以場景為準，平常不用 shouts。
        """) + RelatedTopics(mood, request) + PreferenceContext(mood.Preferences);

    private static string PreferenceContext(IReadOnlyDictionary<string, string> preferences)
    {
        var length = preferences.GetValueOrDefault("回答長度");
        var style = preferences.GetValueOrDefault("互動風格");
        var pacing = preferences.GetValueOrDefault("語音節奏");
        var selected = new List<string>();
        if (length is "簡短" or "平衡" or "詳細") selected.Add("回答長度「" + length + "」");
        if (style is "安靜" or "適中" or "活潑") selected.Add("互動風格「" + style + "」");
        if (pacing is "慢" or "自然" or "快") selected.Add("語音節奏「" + pacing + "」");
        return selected.Count == 0 ? "" : "\n使用者已設定的偏好優先於米亞的預設表達：「" + string.Join('、', selected) + "」。";
    }

    private static string RelatedTopics(MoodSnapshot mood, string request)
    {
        var matches = InteractionTopics.For(request).Where(topic => topic != "其他閒聊" && mood.RecentTopics.ContainsKey(topic))
            .OrderByDescending(topic => mood.RecentTopics[topic]).Take(2).ToArray();
        return matches.Length == 0 ? "" : "\n本次相關的近 30 天話題分類：「" + string.Join('、', matches) +
            "」。分類只供語氣與話題參考，不能據此編造曾經聊過的細節。";
    }
}
