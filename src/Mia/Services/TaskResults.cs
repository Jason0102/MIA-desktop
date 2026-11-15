using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mia.Services;

internal sealed record TaskResult(string Id, string Title, string Request, string Reply,
    DateTimeOffset? UpdatedAt, string[] Files, bool Renamed = false);

internal sealed class TaskResults(string workspace, string indexPath)
{
    private sealed record Delivery(string? SessionId, string[] Files);
    private sealed record Job(string Id, string? SessionId, DateTimeOffset UpdatedAt, string[] Files);
    private static readonly Regex JobId = new(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase);
    private static readonly Regex ResultPath = new(@"(?:^|[\\/])jobs[\\/]([0-9a-f]{32})(?=[\\/])", RegexOptions.IgnoreCase);
    private readonly Dictionary<string, TaskResult> entries = new(StringComparer.OrdinalIgnoreCase);
    internal IEnumerable<TaskResult> Entries => entries.Values.OrderByDescending(item => item.UpdatedAt)
        .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id);
    internal TaskResult? Find(string id) => entries.GetValueOrDefault(id);
    private string JobsPath => Path.Combine(workspace, "jobs");

    internal void Load()
    {
        if (!File.Exists(indexPath)) return;
        foreach (var item in JsonSerializer.Deserialize<TaskResult[]>(File.ReadAllText(indexPath)) ?? [])
            if (item is { Id: not null, Title: not null, Request: not null, Reply: not null, Files: not null })
                entries[item.Id] = item with { Files = item.Files.Where(file => file is not null && Resolve(file) is not null).ToArray() };
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        string temporary = indexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Entries));
        File.Move(temporary, indexPath, overwrite: true);
    }

    internal void Refresh(IEnumerable<TaskResult> taskHistory, IEnumerable<ConversationTurn>? legacyTurns = null)
    {
        var jobs = ReadJobs().ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        var legacy = new Dictionary<string, ConversationTurn>(StringComparer.OrdinalIgnoreCase);
        if (legacyTurns is not null)
            foreach (var turn in legacyTurns)
                foreach (var match in ResultPath.Matches(turn.Assistant).Cast<Match>())
                    if (jobs.ContainsKey(match.Groups[1].Value)) legacy[match.Groups[1].Value] = turn;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in taskHistory)
        {
            var previous = Find(task.Id);
            var references = ResultPath.Matches(task.Reply).Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var related = jobs.Values.Where(job => string.Equals(job.SessionId, task.Id, StringComparison.OrdinalIgnoreCase)
                || (job.SessionId is null && references.Contains(job.Id))).ToArray();
            foreach (var job in related)
            {
                entries.Remove("job-" + job.Id);
                claimed.Add(job.Id);
            }
            var files = MergeFiles(previous?.Files ?? [], related);
            var date = task.UpdatedAt ?? previous?.UpdatedAt ?? related.Select(job => (DateTimeOffset?)job.UpdatedAt).Max();
            entries[task.Id] = task with
            {
                Title = previous is { Renamed: true } ? previous.Title : task.Title,
                Renamed = previous?.Renamed ?? false,
                UpdatedAt = date,
                Files = files,
                Reply = previous is not null && previous.UpdatedAt == date && previous.Reply.StartsWith(task.Reply, StringComparison.Ordinal)
                    ? previous.Reply : task.Reply
            };
        }

        // Retain past task associations after their conversation entries age out.
        foreach (var entry in entries.Values.Where(entry => !entry.Id.StartsWith("job-", StringComparison.Ordinal)).ToArray())
        {
            var related = jobs.Values.Where(job => string.Equals(job.SessionId, entry.Id, StringComparison.OrdinalIgnoreCase)
                || (job.SessionId is null && entry.Files.Any(file => FileJobId(file).Equals(job.Id, StringComparison.OrdinalIgnoreCase)))).ToArray();
            foreach (var job in related) claimed.Add(job.Id);
            entries[entry.Id] = entry with { Files = MergeFiles(entry.Files, related) };
        }
        foreach (var job in jobs.Values)
        {
            string id = "job-" + job.Id;
            if (claimed.Contains(job.Id)) { entries.Remove(id); continue; }
            var previous = Find(id);
            legacy.TryGetValue(job.Id, out var turn);
            string title = turn?.User.Split('\n')[0].Trim() ?? Path.GetFileNameWithoutExtension(job.Files[0]);
            if (title.Length > 48) title = title[..48] + "…";
            entries[id] = new(id, previous?.Title ?? title, previous?.Request ?? turn?.User ?? "",
                previous?.Reply ?? turn?.Assistant ?? "", job.UpdatedAt,
                MergeFiles(previous?.Files ?? [], [job]), previous?.Renamed ?? false);
        }
    }

    private static string[] MergeFiles(IEnumerable<string> previous, Job[] related)
    {
        // Keep historical paths visible when a file has been removed or moved.
        return related.SelectMany(job => job.Files).Concat(previous).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IEnumerable<Job> ReadJobs()
    {
        if (!SafeExisting(JobsPath) || !Directory.Exists(JobsPath)) yield break;
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (string directory in Directory.EnumerateDirectories(JobsPath, "*", options))
        {
            string id = Path.GetFileName(directory);
            if (!JobId.IsMatch(id)) continue;
            string output = Path.Combine(directory, "output");
            string receipt = Path.Combine(directory, "mia-delivery.json");
            Delivery? delivery = null;
            if (File.Exists(receipt) && SafeExisting(receipt))
            {
                try { delivery = JsonSerializer.Deserialize<Delivery>(File.ReadAllText(receipt)); }
                catch (JsonException) { }
            }
            if (delivery is null && File.Exists(Path.Combine(directory, ".mia-delivery-managed"))) continue;
            var resultTimes = Directory.EnumerateFiles(directory, "codex-result*.txt", options)
                .Where(file => new FileInfo(file).Length > 0).Select(File.GetLastWriteTimeUtc).ToArray();
            if (delivery is null && resultTimes.Length == 0) continue;
            string[] files = delivery?.Files is { Length: > 0 } recorded
                ? recorded.Select(file => id + "/" + file).Where(file => Resolve(file) is not null).ToArray()
                : SafeExisting(output) && Directory.Exists(output)
                    ? Directory.EnumerateFiles(output, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                    }).Select(file => Path.GetRelativePath(JobsPath, file).Replace('\\', '/')).ToArray()
                    : [];
            if (files.Length == 0) continue;
            var updated = delivery is not null ? File.GetLastWriteTimeUtc(receipt)
                : resultTimes.Max();
            yield return new(id, delivery?.SessionId, updated, files);
        }
    }

    internal void Rename(string id, string title)
    {
        if (Find(id) is { } entry) entries[id] = entry with { Title = title, Renamed = true };
    }

    internal void ClearConversationText()
    {
        foreach (var entry in entries.Values.ToArray())
        {
            if (entry.Files.Length == 0) entries.Remove(entry.Id);
            else entries[entry.Id] = entry with { Request = "", Reply = "" };
        }
    }

    internal string? Resolve(string relative, bool folder = false)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < (folder ? 2 : 3) || !JobId.IsMatch(parts[0]) || parts[1] != "output"
            || parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return Path.Combine(JobsPath, Path.Combine(parts));
    }

    internal string? Available(string relative, bool folder = false)
    {
        var path = Resolve(relative, folder);
        if (path is null || (folder ? !Directory.Exists(path) : !File.Exists(path))) return null;
        return SafeExisting(path) ? path : null;
    }

    private static bool SafeExisting(string path)
    {
        try
        {
            for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string FileJobId(string relative) => relative.Split('/')[0];

    internal IEnumerable<TaskResult> Search(string query)
    {
        var today = DateTime.Today;
        var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateTime? from = null, until = null;
        foreach (var (word, start, end) in new[]
        {
            ("今天", today, today.AddDays(1)), ("昨天", today.AddDays(-1), today),
            ("本週", week, week.AddDays(7)), ("上週", week.AddDays(-7), week)
        })
        {
            if (!query.Contains(word, StringComparison.Ordinal)) continue;
            from = start; until = end; query = query.Replace(word, " ", StringComparison.Ordinal);
            break;
        }
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Entries.Where(item =>
        {
            var day = item.UpdatedAt?.LocalDateTime.Date;
            return (from is null || (day >= from && day < until)) && terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Request.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Files.Any(file => Path.GetFileName(file).Contains(term, StringComparison.OrdinalIgnoreCase)));
        });
    }
}
