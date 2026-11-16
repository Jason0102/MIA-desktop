using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mia.Services;

internal sealed record ScheduledNotice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt)
{
    public string Label => $"{ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Title}";
}

internal sealed class ScheduledNotices(string workspace)
{
    private readonly string _root = Path.Combine(workspace, "state", "scheduled-notices");

    public IReadOnlyList<ScheduledNotice> Receive()
    {
        string pending = Path.Combine(_root, "pending");
        string received = Path.Combine(_root, "received");
        var notices = new List<ScheduledNotice>();
        if (!IsPlainDirectory(pending) || !IsPlainDirectory(_root)) return notices;
        Directory.CreateDirectory(received);
        if (!IsPlainDirectory(received)) return notices;
        foreach (string file in Directory.EnumerateFiles(pending, "*.json").OrderBy(File.GetCreationTimeUtc))
        {
            if (Read(file) is not { } notice) continue;
            string destination = Path.Combine(received, notice.Id + ".json");
            try
            {
                // Claim the notice before forwarding it. The full text stays in history.
                File.Move(file, destination);
                notices.Add(notice);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another reader or antivirus may temporarily hold the file. Continue with the batch.
                System.Diagnostics.Debug.WriteLine(ex);
            }
            if (notices.Count == 20) break;
        }
        return notices;
    }

    public IReadOnlyList<ScheduledNotice> History()
    {
        string received = Path.Combine(_root, "received");
        if (!IsPlainDirectory(_root) || !IsPlainDirectory(received)) return [];
        return Directory.EnumerateFiles(received, "*.json")
            .OrderByDescending(File.GetCreationTimeUtc).Take(200)
            .Select(Read).OfType<ScheduledNotice>()
            .OrderByDescending(notice => notice.ReceivedAt).ToList();
    }

    private static bool IsPlainDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static ScheduledNotice? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 512_000 ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _)) return null;
            using var stream = File.OpenRead(file);
            var notice = JsonSerializer.Deserialize<ScheduledNotice>(stream);
            return notice is not null && notice.Id == Path.GetFileNameWithoutExtension(file) &&
                !string.IsNullOrWhiteSpace(notice.Title) && !string.IsNullOrWhiteSpace(notice.Text)
                ? notice : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
