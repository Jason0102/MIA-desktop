using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mia.Services;

public sealed record ConversationTurn(string User, string Assistant, DateTimeOffset Time);

public sealed class ConversationStore
{
    private readonly string _file;
    public ConversationStore(string fileName = "conversation.jsonl") => _file = Path.Combine(SettingsStore.DataDirectory, fileName);
    public IReadOnlyList<ConversationTurn> LoadRecent()
    {
        var recent = new Queue<ConversationTurn>();
        if (!File.Exists(_file)) return recent.ToArray();
        foreach (string line in File.ReadLines(_file))
        {
            try
            {
                var turn = JsonSerializer.Deserialize<ConversationTurn>(line);
                if (turn is null || string.IsNullOrWhiteSpace(turn.User) || string.IsNullOrWhiteSpace(turn.Assistant)) continue;
                recent.Enqueue(turn);
                if (recent.Count > 10) recent.Dequeue();
            }
            catch (JsonException) { /* A torn final record must not hide earlier turns. */ }
        }
        return recent.ToArray();
    }
    public void Append(string user, string assistant)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        // A leading newline isolates a record left incomplete by a crash.
        File.AppendAllText(_file, "\n" + JsonSerializer.Serialize(new ConversationTurn(user, assistant, DateTimeOffset.Now)) + "\n");
    }
    public void Clear() { if (File.Exists(_file)) File.Delete(_file); }
}
