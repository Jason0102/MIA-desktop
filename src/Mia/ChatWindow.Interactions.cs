using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private sealed record InteractionTurn(InteractionMemory Memory, string Generation, string Id, DateTimeOffset At, string Text)
    {
        public static InteractionTurn? Capture(string text)
        {
            var memory = InteractionMemory.Desktop();
            if (memory is null) return null;
            string generation = memory.Generation();
            if (generation.Length == 0 || !memory.GetPolicy().Enabled) return null;
            var turn = new InteractionTurn(memory, generation, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, text);
            turn.Record("message", "accepted");
            if (InteractionTopics.Feedback(text) is { } feedback) turn.Record("feedback", feedback);
            return turn;
        }

        public void Record(string kind, string? status = null, string? group = null, string[]? classified = null)
        {
            string[]? topics = kind == "chat" ? classified ?? InteractionTopics.For(Text) : null;
            Memory.Record(new(Id + ":" + kind, At, "PC", kind, group, Topics: topics, Status: status,
                TopicConfidence: topics is null ? null : classified is null ? "keyword" : "classified"), Generation);
        }

        public void Classify(string action, string? group = null, string[]? topics = null)
        {
            if (action == "new") Record("task_new", "accepted", group);
            if (action is "supplement" or "answer") Record("task_resume", "accepted", group);
            if (topics is { Length: > 0 } || action == "chat" || InteractionTopics.HasChatLead(Text))
                Record("chat", "classified", group, topics is { Length: > 0 } ? topics : null);
        }

        public void Interrupted(string status)
        {
            Record("message_result", status);
            if (!InteractionTopics.LooksLikeTask(Text) || InteractionTopics.HasChatLead(Text))
                Record("chat", status);
        }
    }
}
