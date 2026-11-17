using System.Globalization;
using System.Net;
using System.Text;
using Mia.Services;

namespace Mia;

internal sealed partial class InteractionDashboard
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly (string Kind, string Label)[] Counts =
    {
        ("chat", "非任務發話"), ("task_new", "新任務"), ("task_resume", "續接任務"),
        ("cursor_near", "游標靠近"), ("cursor_dwell", "游標停留"), ("click", "點擊"),
        ("pet", "摸頭"), ("drag", "拖曳"), ("ball_session", "陪玩場次"),
        ("ball_throw", "投球"), ("feedback", "明確回饋")
    };

    private sealed record DashboardFilters(string Period, string Channel)
    {
        internal static DashboardFilters Parse(IReadOnlyDictionary<string, string> query)
        {
            var period = query.GetValueOrDefault("period");
            var channel = query.GetValueOrDefault("channel");
            return new(period is "today" or "week" or "seven" or "month" ? period : "week",
                channel is "PC" or "Discord" ? channel : "all");
        }

        internal string Query => $"?period={Period}&channel={Channel}";
    }

    private static string Render(InteractionMemory memory, DashboardFilters filters, string path, string? notice)
    {
        string generation = memory.Generation();
        var events = memory.Read();
        var policy = memory.GetPolicy();
        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(now.Date);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = filters.Period switch
        {
            "today" => today, "week" => monday, "seven" => today.AddDays(-6), _ => today.AddDays(-29)
        };
        string? channel = filters.Channel == "all" ? null : filters.Channel;
        var summary = InteractionMemory.Summarize(events, start, today, zone, channel);
        var coveredFrom = memory.CoveredFrom();
        DateOnly? coverageStart = coveredFrom.HasValue ? LocalDay(coveredFrom.Value, zone) : null;
        bool hasCoverage = coverageStart <= today;
        var visible = events.Where(item => (channel is null || item.Channel == channel) &&
            LocalDay(item.At, zone) >= start && LocalDay(item.At, zone) <= today).ToArray();
        string DisplayCount(int value) => hasCoverage ? Number(value) : "—";
        string DisplayDuration(double value) => hasCoverage ? Duration(value) : "—";
        string Post(string action) =>
            $"""
                <form method="post" action="{H(path + filters.Query)}"><input type="hidden" name="action" value="{action}">
                <input type="hidden" name="generation" value="{H(generation)}">
                """;

        var html = new StringBuilder(PageStart);
        html.Append("<main><header><p class=\"kicker\">米亞 MIA 3.3</p><h1>後台資訊</h1></header>");
        var message = notice switch
        {
            "saved" => "已儲存。", "excluded" => "已從互動記憶排除這個話題。", "renamed" => "已更正話題名稱。",
            "removed" => "已刪除這項偏好。", _ => null
        };
        if (message is not null) html.Append($"<p class=\"notice\" role=\"status\">{H(message)}</p>");
        html.Append($"""
            <section aria-labelledby="overview">
              <h2 id="overview">互動概況</h2>
              <form method="get" action="{path}" class="filters">
                <label>統計期間 <select name="period">
                {Option("today", "今日", filters.Period)}{Option("week", "本週", filters.Period)}
                {Option("seven", "近 7 天", filters.Period)}{Option("month", "近 30 天（每週）", filters.Period)}
                </select></label>
                <label>來源 <select name="channel">
                {Option("all", "全部管道", filters.Channel)}{Option("PC", "電腦", filters.Channel)}
                {Option("Discord", "Discord", filters.Channel)}
                </select></label>
                <button type="submit" class="primary">套用</button>
              </form>
              <p class="subtle">{Day(start)} ～ {Day(today)} · {H(zone.DisplayName)}</p>
              <dl class="metrics">
            """);
        foreach (var (kind, label) in Counts) Metric(html, label, DisplayCount(summary.Counts.GetValueOrDefault(kind)));
        Metric(html, "使用者發話", DisplayCount(summary.Counts.GetValueOrDefault("message")));
        Metric(html, "聊天段數", DisplayCount(summary.ChatSegments));
        Metric(html, "活躍天數", DisplayCount(summary.ActiveDays));
        Metric(html, "玩球總時長", DisplayDuration(summary.Seconds.GetValueOrDefault("ball_elapsed")));
        Metric(html, "有效陪玩", DisplayDuration(summary.Seconds.GetValueOrDefault("ball_active")));
        Metric(html, "米亞自主玩球", DisplayDuration(summary.Seconds.GetValueOrDefault("ball_solo")));
        if (channel != "Discord")
        {
            Metric(html, "桌面陪伴時間", DisplayDuration(summary.Seconds.GetValueOrDefault("presence")));
            Metric(html, "桌面有效互動時間", DisplayDuration(summary.Seconds.GetValueOrDefault("interaction_active")));
        }
        var results = visible.Where(item => item.Kind == "task_result").ToArray();
        Metric(html, "任務完成", DisplayCount(results.Count(item => item.Status == "completed")));
        Metric(html, "任務失敗", DisplayCount(results.Count(item => item.Status == "failed")));
        Metric(html, "任務取消", DisplayCount(results.Count(item => item.Status == "cancelled")));
        Metric(html, "任務中斷", DisplayCount(results.Count(item => item.Status == "interrupted")));
        html.Append("</dl>");
        if (hasCoverage)
        {
            string periodLabel = filters.Period switch
            {
                "today" => "今日小記", "week" => "本週小記", "seven" => "近 7 天小記", _ => "近 30 天小記"
            };
            html.Append($"<p class=\"summary\"><strong>{periodLabel}</strong> {H(Describe(summary))}</p>");
        }
        if (coverageStart is { } covered && covered > start)
            html.Append($"<p class=\"subtle\">可用紀錄自 {Day(covered)} 起，先前的互動情況未知。</p>");
        else if (!hasCoverage)
            html.Append("<p class=\"subtle\">目前沒有可用的互動紀錄。</p>");
        html.Append("<p class=\"subtle\">暫停記錄或超過保存期限的期間可能沒有資料。</p>");
        AppendTrend(html, events, start, today, zone, channel, coverageStart, filters.Period == "month");
        html.Append("</section>");

        var mood = InteractionMood.Calculate(events, now);
        html.Append($"""
            <section aria-labelledby="mood"><h2 id="mood">米亞目前的心情</h2>
            <p><strong>{H(mood.State)}</strong> <span class="subtle">· {H(mood.UpdatedAt.ToLocalTime().ToString("g", DisplayCulture))}</span></p>
            <dl class="metrics">
            """);
        MoodMetric(html, "愉悅度", mood.Pleasantness);
        MoodMetric(html, "活力", mood.Energy);
        MoodMetric(html, "親近感", mood.Affinity);
        MoodMetric(html, "玩心", mood.Playfulness);
        html.Append("</dl><ul>");
        foreach (string reason in mood.Reasons) html.Append($"<li>{H(reason)}</li>");
        html.Append("</ul><p class=\"subtle\">心情依各管道的近期互動計算，短期變化會隨時間回到角色基準。</p></section>");

        html.Append("<section aria-labelledby=\"topics\"><h2 id=\"topics\">常聊話題</h2><p class=\"subtle\">話題依對話關鍵字整理，可更正名稱或排除。</p>");
        if (summary.Topics.Count == 0) html.Append("<p class=\"subtle\">這段期間沒有話題紀錄。</p>");
        else
        {
            html.Append("<ul class=\"items\">");
            foreach (var (topic, count) in summary.Topics.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(50))
            {
                var latest = visible.Where(item => item.Topics?.Contains(topic, StringComparer.OrdinalIgnoreCase) == true)
                    .OrderByDescending(item => item.At).FirstOrDefault();
                string last = latest is null ? "" : " · 最近 " + Day(LocalDay(latest.At, zone));
                html.Append($"""
                    <li class="topic"><span>{H(topic)} <span class="subtle">· {Number(count)} 次{last}</span></span>
                    <div class="topic-actions">
                    <details><summary>更正名稱</summary>{Post("rename")}
                    <input type="hidden" name="topic" value="{H(topic)}">
                    <div class="edit"><label>新名稱 <input name="replacement" required maxlength="80" value="{H(topic)}"></label>
                    <button type="submit">更正</button></div></form></details>
                    {Post("exclude")}<input type="hidden" name="topic" value="{H(topic)}">
                    <button type="submit" aria-label="{H("排除話題 " + topic)}">排除 ⨯</button></form>
                    </div></li>
                    """);
            }
            html.Append("</ul>");
        }
        html.Append("</section><section aria-labelledby=\"preferences\"><h2 id=\"preferences\">互動偏好</h2>");
        foreach (var (name, value) in memory.GetPreferences().OrderBy(item => item.Key))
        {
            html.Append($"""
                {Post("preference")}
                <input type="hidden" name="name" value="{H(name)}">
                <div class="edit">
                <label>{H(name)} <input name="value" maxlength="300" value="{H(value)}"></label>
                <button type="submit">更新</button></div></form>
                """);
        }
        html.Append($"""
            <p class="subtle">可設定「回答長度」（簡短、平衡、詳細）、「互動風格」（安靜、適中、活潑）或「語音節奏」（慢、自然、快）。清空內容並更新即可刪除偏好。</p>
            {Post("preference")}<div class="edit">
            <label>新增偏好名稱 <input name="name" required maxlength="40"></label>
            <label>內容 <input name="value" required maxlength="300"></label>
            <button type="submit">新增</button></div></form></section>
            <section aria-labelledby="expression"><h2 id="expression">近期語音與動畫</h2>
            """);
        var traces = visible.Where(item => item.Kind == "expression").OrderByDescending(item => item.At).Take(20).ToArray();
        if (traces.Length == 0) html.Append("<p class=\"subtle\">這段期間沒有表達紀錄。</p>");
        else
        {
            html.Append("<ul class=\"items trace\">");
            foreach (var trace in traces)
                html.Append($"<li><span class=\"subtle\">{H(trace.At.ToLocalTime().ToString("g", DisplayCulture))} · {H(trace.Channel)}</span><span>{H(trace.Detail)}</span></li>");
            html.Append("</ul>");
        }
        html.Append($"""
            </section><section aria-labelledby="privacy"><h2 id="privacy">記錄與保存</h2>
            {Post("configure")}<div class="filters">
            <label class="checkbox"><input type="checkbox" name="enabled"{(policy.Enabled ? " checked" : "")}> 記錄互動</label>
            <label>保存天數 <input type="number" name="days" min="1" max="365" required value="{policy.RetentionDays.ToString(CultureInfo.InvariantCulture)}"></label>
            <button type="submit" class="primary">儲存設定</button></div></form>
            <p class="subtle">互動記憶保存在此電腦。關閉記錄會停止新增資料。可從米亞設定清除歷史記憶。</p>
            </section></main></body></html>
            """);
        return html.ToString();
    }

    private static void AppendTrend(StringBuilder html, IReadOnlyList<InteractionEvent> events, DateOnly start,
        DateOnly today, TimeZoneInfo zone, string? channel, DateOnly? coverageStart, bool weekly)
    {
        html.Append($"""
            <div class="table-wrap"><table>
            <caption>{(weekly ? "每週趨勢" : "每日趨勢")}</caption>
            <thead><tr><th scope="col">期間</th><th scope="col">聊天段數</th><th scope="col">新任務</th>
            <th scope="col">陪玩時長</th><th scope="col">常見話題</th></tr></thead><tbody>
            """);
        for (var day = start; day <= today;)
        {
            var last = weekly ? day.AddDays(6 - (((int)day.DayOfWeek + 6) % 7)) : day;
            if (last > today) last = today;
            var row = InteractionMemory.Summarize(events, day, last, zone, channel);
            bool known = coverageStart <= last;
            string topics = string.Join("、", row.Topics.OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key).Take(3).Select(item => item.Key));
            html.Append($"""
                <tr><th scope="row">{Day(day)}{(weekly ? " ～ " + Day(last) : "")}</th>
                <td>{(known ? Number(row.ChatSegments) : "—")}</td><td>{(known ? Number(row.Counts.GetValueOrDefault("task_new")) : "—")}</td>
                <td>{(known ? Duration(row.Seconds.GetValueOrDefault("ball_active")) : "—")}</td>
                <td>{(topics.Length > 0 ? H(topics) : "—")}</td></tr>
                """);
            day = last.AddDays(1);
        }
        html.Append("</tbody></table></div>");
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Describe(InteractionSummary summary)
    {
        var parts = new List<string>();
        int assigned = summary.Counts.GetValueOrDefault("task_new");
        int throws = summary.Counts.GetValueOrDefault("ball_throw");
        double play = summary.Seconds.GetValueOrDefault("ball_active");
        if (summary.ChatSegments > 0) parts.Add($"聊了 {Number(summary.ChatSegments)} 段");
        if (assigned > 0) parts.Add($"交付了 {Number(assigned)} 項新任務");
        if (play > 0) parts.Add($"一起玩球 {Duration(play)}");
        else if (throws > 0) parts.Add($"投了 {Number(throws)} 次球");
        string description = parts.Count > 0 ? string.Join("，", parts) + "。" : "這段期間沒有聊天、新任務或陪玩紀錄。";
        var top = summary.Topics.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(3).Select(item => item.Key).ToArray();
        if (top.Length > 0) description += $"常聊到{string.Join("、", top.Select(topic => $"「{topic}」"))}。";
        return description;
    }
    private static DateOnly LocalDay(DateTimeOffset value, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).Date);
    private static string Day(DateOnly value) => value.ToString("yyyy/MM/dd", DisplayCulture);
    private static string Number(int value) => value.ToString("N0", DisplayCulture);
    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        long total = (long)seconds;
        return total >= 3600 ? $"{total / 3600} 小時 {total / 60 % 60} 分" :
            total >= 60 ? $"{total / 60} 分 {total % 60} 秒" : $"{total} 秒";
    }
    private static string Option(string value, string label, string selected) =>
        $"""<option value="{value}"{(value == selected ? " selected" : "")}>{label}</option>""";
    private static void Metric(StringBuilder html, string label, string value) =>
        html.Append($"<div><dt>{label}</dt><dd>{value}</dd></div>");
    private static void MoodMetric(StringBuilder html, string label, double value)
    {
        string number = double.IsFinite(value) ? Math.Clamp(value, 0, 100).ToString("0", CultureInfo.InvariantCulture) : "0";
        html.Append($"""<div><dt>{label}</dt><dd>{number}<meter min="0" max="100" aria-label="{label}" value="{number}"></meter></dd></div>""");
    }

    private const string PageStart = """
        <!doctype html>
        <html lang="zh-Hant-TW">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>米亞 MIA 3.3 · 後台資訊</title>
        <style>
          :root{color-scheme:dark;--ink:#e9e8de;--subtle:#a5b1b3;--gold:#eec364;--bg:#131b20;--panel:#212a2f;--line:#3c494e}
          *,*::before,*::after{box-sizing:border-box}
          body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.65 "Microsoft JhengHei UI","Microsoft JhengHei",sans-serif}
          main{width:min(980px,100%);margin:0 auto;padding:32px 20px 44px}
          header{padding:0 0 18px}.kicker,h2{color:var(--gold)}
          h1{font-size:30px;line-height:1.2;margin:2px 0}.kicker{margin:0 0 8px}
          h2{font-size:18px;margin:0 0 12px}section{border:1px solid var(--line);background:var(--panel);border-radius:12px;margin:0 0 16px;padding:18px}
          p{margin:0 0 12px}.subtle{color:var(--subtle)}.notice,.summary{border-left:3px solid var(--gold);padding:7px 10px}
          .filters,.edit{display:flex;flex-wrap:wrap;gap:12px;align-items:end;margin:0 0 10px}.edit label{flex:1 1 160px}
          label{display:grid;gap:4px;color:var(--subtle)}label.checkbox{display:flex;align-items:center;min-height:42px;color:var(--ink)}
          input,select,button{font:inherit;color:var(--ink);background:var(--bg);border:1px solid var(--line);border-radius:7px;padding:8px 12px;min-height:42px}
          input{min-width:0;width:100%}input[type=checkbox]{width:18px;min-height:18px;accent-color:var(--gold)}
          button{cursor:pointer;white-space:nowrap}button:hover{border-color:var(--gold)}.primary{background:var(--gold);border-color:var(--gold);color:#131b20}
          :focus-visible{outline:2px solid var(--gold);outline-offset:2px}
          .metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin:12px 0}
          .metrics div{background:var(--bg);border-radius:9px;padding:9px 12px;min-width:0}
          dt{color:var(--subtle)}dd{margin:0;font-size:18px;font-weight:700}meter{display:block;width:100%;height:13px;accent-color:var(--gold)}
          .table-wrap{overflow-x:auto}table{border-collapse:collapse;text-align:left;width:100%;min-width:380px}
          caption{text-align:left;color:var(--gold);padding:6px 0}td,th{border-bottom:1px solid var(--line);padding:8px}
          tbody th{font-weight:400}.items{padding:0;margin:0;list-style:none}.items li{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 0;overflow-wrap:anywhere;border-bottom:1px solid var(--line)}
          .items li:last-child{border-bottom:0}.trace li{align-items:start;flex-direction:column;gap:3px}
          .topic,.topic-actions{flex-wrap:wrap}.topic-actions{display:flex;align-items:start;gap:8px}
          details{margin:8px 0}summary{cursor:pointer;color:var(--gold)}
          @media(max-width:480px){main{padding:20px 12px}section{padding:13px}.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}}
        </style>
        </head>
        <body>
        """;
}
