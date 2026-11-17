using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mia.Services;

public sealed record InteractionEvent(string Id, DateTimeOffset At, string Channel, string Kind, string? Group = null,
    double Seconds = 0, string[]? Topics = null, string? Status = null, string? Detail = null, string? TopicConfidence = null);

public sealed record InteractionPolicy(bool Enabled = true, int RetentionDays = 90);

public sealed record InteractionSummary(DateOnly Start, DateOnly End, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, double> Seconds, IReadOnlyDictionary<string, int> Topics, int ChatSegments, int ActiveDays);

/// <summary>A local per-identity event store shared with the Discord gateway.</summary>
public sealed class InteractionMemory
{
    private sealed record Metadata(string[] Excluded, Dictionary<string, string> Preferences, Dictionary<string, string>? Aliases = null);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly InteractionPolicy Defaults = new();
    private readonly string _root;
    private readonly string _identity;

    public InteractionMemory(string stateDirectory, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("A bound user is required.", nameof(userId));
        _identity = Digest(userId);
        _root = Path.Combine(stateDirectory, "interaction-memory", _identity);
    }

    public static InteractionMemory? Desktop()
    {
        string local = Path.Combine(SettingsStore.DataDirectory, "users.json");
        string path = File.Exists(local) ? local : Path.Combine(AppContext.BaseDirectory, "users.json");
        try
        {
            var directory = JsonSerializer.Deserialize<UserIdentity.Directory>(File.ReadAllText(path));
            var matches = directory?.Bindings.Where(binding => binding.Provider == "desktop" && binding.Subject == "local").ToArray();
            if (matches?.Length != 1 || directory!.Users.Count(user => user.Id == matches[0].UserId) != 1) return null;
            return new InteractionMemory(SettingsStore.DataDirectory, matches[0].UserId);
        }
        catch (Exception ex) when (StorageFailure(ex) || ex is ArgumentException or NullReferenceException) { return null; }
    }

    // Capture this before beginning work. Supplying a stale generation makes a late result a no-op.
    public string Generation() => Safely(() => WithLock(Current), "")!;
    public InteractionPolicy GetPolicy() => Safely(() => WithLock(Policy), Defaults)!;
    public DateTimeOffset? CoveredFrom() => Safely<DateTimeOffset?>(() => WithLock<DateTimeOffset?>(() =>
    {
        Current();
        DateTimeOffset observed = File.GetLastWriteTimeUtc(Path.Combine(_root, "generation.json"));
        var cutoff = Expiry(Policy());
        return observed > cutoff ? observed : cutoff;
    }), null);

    public void Record(InteractionEvent item, string? generation = null)
    {
        Safely(() => WithLock(() =>
        {
            var policy = Policy();
            if (!policy.Enabled || ResetInProgress() || string.IsNullOrWhiteSpace(item.Id) || item.Channel is not ("PC" or "Discord") ||
                !double.IsFinite(item.Seconds) || item.Seconds is < 0 or > 86400 || item.At.AddSeconds(item.Seconds) < Expiry(policy)) return false;
            string current = Current();
            if (generation is not null && generation != current) return false;
            Prune(current, policy);
            string path = Path.Combine(Events(current), Digest(item.Id) + ".json");
            if (File.Exists(path)) return false;
            item = ApplyTopicPreferences(item, ReadMetadata(current));
            Write(path, item);
            return true;
        }), false);
    }

    public IReadOnlyList<InteractionEvent> Read() => Safely<IReadOnlyList<InteractionEvent>>(() =>
    {
        var (generation, paths) = WithLock(() =>
        {
            string current = Current();
            Prune(current, Policy());
            return (current, Files(current).ToArray());
        });
        // Reading files outside the writer lock keeps a history query from blocking a Discord admission.
        var items = paths.Select(TryReadEvent).OfType<InteractionEvent>().ToArray();
        var (valid, cutoff, metadata) = WithLock(() => (
            Current() == generation && !ResetInProgress(), Expiry(Policy()), ReadMetadata(generation)));
        if (!valid) return [];
        return items.Where(item => item.At.AddSeconds(item.Seconds) >= cutoff)
            .Select(item => ApplyTopicPreferences(ClipRetention(item, cutoff), metadata)).OrderBy(item => item.At).ToArray();
    }, [])!;

    public void Configure(bool enabled, int retentionDays, string? generation = null)
    {
        if (retentionDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        WithLock(() =>
        {
            string current = CheckWritableGeneration(generation);
            var policy = new InteractionPolicy(enabled, retentionDays);
            Write(Path.Combine(_root, "policy.json"), policy);
            Prune(current, policy, force: true);
            return true;
        });
    }

    public void ExcludeTopic(string topic, string? generation = null)
    {
        topic = topic.Trim();
        if (topic.Length is 0 or > 80) throw new ArgumentException("話題名稱須為 1 至 80 字。", nameof(topic));
        WithLock(() =>
        {
            string current = CheckWritableGeneration(generation);
            var metadata = ReadMetadata(current);
            string[] excluded = metadata.Excluded.Append(topic).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            metadata = metadata with { Excluded = excluded };
            Write(MetadataPath(current), metadata);
            foreach (string path in Files(current))
            {
                var item = TryRead<InteractionEvent>(path);
                if (item?.Topics is { Length: > 0 }) Write(path, ApplyTopicPreferences(item, metadata));
            }
            return true;
        });
    }

    public void RenameTopic(string oldName, string newName, string? generation = null)
    {
        oldName = oldName.Trim();
        newName = newName.Trim();
        if (oldName.Length is 0 or > 80 || newName.Length is 0 or > 80)
            throw new ArgumentException("話題名稱須為 1 至 80 字。");
        WithLock(() =>
        {
            string current = CheckWritableGeneration(generation);
            var metadata = ReadMetadata(current);
            var aliases = new Dictionary<string, string>(metadata.Aliases ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (string source in aliases.Where(entry => string.Equals(entry.Value, oldName, StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key).ToArray())
                aliases[source] = newName;
            aliases.Remove(newName);
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) aliases[oldName] = newName;
            Write(MetadataPath(current), metadata with { Aliases = aliases });
            return true;
        });
    }

    public IReadOnlyDictionary<string, string> GetPreferences() =>
        Safely<IReadOnlyDictionary<string, string>>(() => WithLock(() => ReadMetadata(Current()).Preferences),
            new Dictionary<string, string>())!;

    public void SetPreference(string name, string value, string? generation = null)
    {
        name = name.Trim();
        value = value.Trim();
        if (name.Length is 0 or > 40 || value.Length > 300) throw new ArgumentException("偏好名稱或內容的長度超出限制。");
        WithLock(() =>
        {
            string current = CheckWritableGeneration(generation);
            var metadata = ReadMetadata(current);
            if (value.Length == 0) metadata.Preferences.Remove(name);
            else metadata.Preferences[name] = value;
            Write(MetadataPath(current), metadata);
            return true;
        });
    }

    public void Reset() => ResetAsync(() => Task.CompletedTask).GetAwaiter().GetResult();

    public async Task ResetAsync(Func<Task> clearExistingMemory)
    {
        string marker = Path.Combine(_root, "resetting.json");
        WithLock(() =>
        {
            CheckWritableGeneration(null);
            foreach (var lease in Directory.EnumerateFiles(Epoch(Current()), "active-*.json"))
            {
                if (ProcessIsRunning(TryRead<int>(lease)))
                    throw new InvalidOperationException("請先等待或停止此使用者的 Discord 工作，再重置記憶。");
                File.Delete(lease);
            }
            Write(marker, Environment.ProcessId);
            return true;
        });
        try
        {
            await clearExistingMemory();
            WithLock(() =>
            {
                string next = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Events(next));
                Write(Path.Combine(_root, "generation.json"), next);
                Write(Path.Combine(_root, "reset-cutoff.json"), DateTimeOffset.UtcNow);
                foreach (string directory in Directory.EnumerateDirectories(_root))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && directory != Epoch(next))
                        Directory.Delete(directory, true);
                return true;
            });
        }
        finally
        {
            WithLock(() => { File.Delete(marker); return true; });
        }
    }

    private string CheckWritableGeneration(string? generation)
    {
        string current = Current();
        if (ResetInProgress() || (generation is not null && generation != current))
            throw new InvalidOperationException("記憶版本已變更，請重新開啟後台再試。");
        return current;
    }

    private bool ResetInProgress() => ProcessIsRunning(TryRead<int>(Path.Combine(_root, "resetting.json")));

    private static bool ProcessIsRunning(int pid)
    {
        if (pid <= 0) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private string Current()
    {
        string marker = Path.Combine(_root, "generation.json");
        string? generation = File.Exists(marker) ? JsonSerializer.Deserialize<string>(File.ReadAllText(marker), Json) : null;
        if (generation is not null)
        {
            if (!Guid.TryParseExact(generation, "N", out _)) throw new IOException("互動記憶版本無效。");
            return generation;
        }
        generation = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Events(generation));
        Write(marker, generation);
        return generation;
    }

    private T WithLock<T>(Func<T> action)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(_root);
            using var stream = new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            for (int attempt = 0; ; attempt++)
            {
                try { stream.Lock(0, 1); break; }
                catch (IOException) when (attempt < 39) { Thread.Sleep(10); }
            }
            try { return action(); }
            finally { stream.Unlock(0, 1); }
        }
    }

    private void Prune(string current, InteractionPolicy policy, bool force = false)
    {
        string stamp = Path.Combine(Epoch(current), "pruned.json");
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!force && TryRead<string>(stamp) == today) return;
        var cutoff = Expiry(policy);
        foreach (string path in Files(current))
        {
            var item = TryReadEvent(path);
            if (item is not null && item.At.AddSeconds(item.Seconds) < cutoff) File.Delete(path);
        }
        Write(stamp, today);
    }

    private InteractionPolicy Policy()
    {
        string path = Path.Combine(_root, "policy.json");
        if (!File.Exists(path)) return Defaults;
        var policy = JsonSerializer.Deserialize<InteractionPolicy>(File.ReadAllText(path), Json);
        return policy is { RetentionDays: >= 1 and <= 365 } ? policy : throw new IOException("互動記憶設定無效。");
    }

    private string Epoch(string generation) => Path.Combine(_root, generation);
    private string Events(string generation) => Path.Combine(Epoch(generation), "events");
    private IEnumerable<string> Files(string generation) =>
        Directory.Exists(Events(generation)) ? Directory.EnumerateFiles(Events(generation), "*.json") : [];
    private string MetadataPath(string generation) => Path.Combine(Epoch(generation), "metadata.json");
    private Metadata ReadMetadata(string generation) => TryRead<Metadata>(MetadataPath(generation)) ?? new([], new());
    private static DateTimeOffset Expiry(InteractionPolicy policy) => DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException;
    private static T? Safely<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch (Exception ex) when (StorageFailure(ex)) { return fallback; }
    }

    private static T? TryRead<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (JsonException) { return default; }
    }

    private static InteractionEvent? TryReadEvent(string path)
    {
        try
        {
            var item = TryRead<InteractionEvent>(path);
            return item is not null && double.IsFinite(item.Seconds) && item.Seconds is >= 0 and <= 86400 &&
                item.At <= DateTimeOffset.MaxValue.AddDays(-1)
                ? item : null;
        }
        catch (IOException) { return null; }
    }

    private static InteractionEvent ClipRetention(InteractionEvent item, DateTimeOffset cutoff)
    {
        return item.At < cutoff ? item with { At = cutoff, Seconds = (item.At.AddSeconds(item.Seconds) - cutoff).TotalSeconds } : item;
    }

    private static InteractionEvent ApplyTopicPreferences(InteractionEvent item, Metadata metadata)
    {
        if (item.Topics is null) return item;
        var aliases = new Dictionary<string, string>(metadata.Aliases ?? new(), StringComparer.OrdinalIgnoreCase);
        return item with { Topics = item.Topics.Except(metadata.Excluded, StringComparer.OrdinalIgnoreCase)
            .Select(topic => aliases.GetValueOrDefault(topic, topic))
            .Except(metadata.Excluded, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static void Write<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    public static InteractionSummary Summarize(IEnumerable<InteractionEvent> events, DateOnly start, DateOnly end,
        TimeZoneInfo zone, string? channel = null)
    {
        if (end < start) throw new ArgumentException("日期範圍無效。", nameof(end));
        DateTimeOffset HourOfStart(DateOnly day)
        {
            DateTime midnight = day.ToDateTime(TimeOnly.MinValue);
            while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            return zone.IsAmbiguousTime(midnight)
                ? new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max())
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        }
        var startsAt = HourOfStart(start);
        var endsAt = HourOfStart(end.AddDays(1));
        var counts = new Dictionary<string, int>();
        var seconds = new Dictionary<string, double>();
        var topics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var activeDays = new HashSet<DateOnly>();
        var previousChat = new Dictionary<string, DateTimeOffset>();
        int segments = 0;
        foreach (var item in events.Where(item => channel is null || item.Channel == channel).OrderBy(item => item.At))
        {
            bool inRange = item.At >= startsAt && item.At < endsAt;
            if (item.Kind == "chat")
            {
                if (inRange && (!previousChat.TryGetValue(item.Channel, out var previous) || item.At - previous >= TimeSpan.FromMinutes(10)))
                    segments++;
                previousChat[item.Channel] = item.At;
            }
            if (inRange)
            {
                counts[item.Kind] = counts.GetValueOrDefault(item.Kind) + 1;
                if (item.Kind == "task_result" && item.Status is { } status)
                    counts[item.Kind + "_" + status] = counts.GetValueOrDefault(item.Kind + "_" + status) + 1;
                if (item.Kind is not ("expression" or "ball_solo" or "presence"))
                    activeDays.Add(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.At, zone).DateTime));
                foreach (string topic in item.Topics ?? [])
                    topics[topic] = topics.GetValueOrDefault(topic) + 1;
            }

            if (item.Seconds <= 0) continue;
            DateTimeOffset intervalStart = item.At < startsAt ? startsAt : item.At;
            DateTimeOffset intervalEnd = item.At.AddSeconds(item.Seconds);
            if (intervalEnd > endsAt) intervalEnd = endsAt;
            if (intervalEnd > intervalStart)
                seconds[item.Kind] = seconds.GetValueOrDefault(item.Kind) + (intervalEnd - intervalStart).TotalSeconds;
        }
        return new(start, end, counts, seconds, topics, segments, activeDays.Count);
    }
}
