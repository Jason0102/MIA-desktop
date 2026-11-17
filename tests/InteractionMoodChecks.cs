using Mia.Services;

internal static class InteractionMoodChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var baseline = InteractionMood.Calculate([], now);
        var mixed = InteractionMood.Calculate([
            new("desktop-pet", now, "PC", "pet"),
            new("discord-chat", now, "Discord", "chat", Topics: ["生活日常"]),
            new("desktop-ball", now, "PC", "ball_active", Seconds: 120),
            new("discord-task", now, "Discord", "task_result", Status: "completed")], now);
        check(mixed.Pleasantness == 63 && mixed.Energy == 50.4 && mixed.Affinity == 35.1 && mixed.Playfulness == 43.7
            && mixed.State == "愉快" && mixed.Reasons[0] == "一起玩球" && mixed.RecentTopics["生活日常"] == 1,
            "shared interaction signals influence all four mood dimensions across channels");
        var replay = InteractionMood.Calculate(Enumerable.Range(0, 200).Select(i => new InteractionEvent(i.ToString(), now, "PC", "pet")), now);
        check(replay.Pleasantness == baseline.Pleasantness + 6, "repeated same-day interactions have diminishing returns and a daily cap");
        var passive = InteractionMood.Calculate([
            new("ambient", now, "PC", "ball_solo", Seconds: 3600),
            new("only-assigned", now, "Discord", "task_new"),
            new("failed", now, "Discord", "task_result", Status: "failed"),
            new("future", now.AddHours(1), "PC", "pet")], now);
        check(passive.Pleasantness == baseline.Pleasantness && passive.Energy == baseline.Energy && passive.Affinity == baseline.Affinity
            && passive.Playfulness == baseline.Playfulness, "autonomous play, unfinished work and future timestamps do not affect mood");
        var older = InteractionMood.Calculate([new("chat", now.AddHours(-72), "PC", "chat")], now);
        check(older.Pleasantness - baseline.Pleasantness < 0.3 && older.Affinity - baseline.Affinity > 1,
            "activation fades more quickly than affinity without punishing inactivity");
    }
}
