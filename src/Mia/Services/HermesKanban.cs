using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mia.Services;

internal sealed record KanbanCard(string Id, string Title, string Status, string Body,
    string Result, string[] Parents, string[] Comments, string? Session, long Run = 0, string? Context = null);

internal sealed record KanbanState(KanbanCard[] Cards, KanbanCard? Claim, string? Changed, string? Error);

internal sealed class HermesKanban(string python, string entry, string profile)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal string Owner { get; } = "mia-" + Guid.NewGuid().ToString("N");
    internal bool BoardExists => File.Exists(Path.Combine(profile, "kanban", "boards", "mia", "board.json"));

    internal static HermesKanban? FromEnvironment()
    {
        string? python = Environment.GetEnvironmentVariable("MIA_HERMES_EXECUTABLE");
        string? entry = Environment.GetEnvironmentVariable("MIA_HERMES_KANBAN_ENTRY");
        string? profile = Environment.GetEnvironmentVariable("HERMES_HOME");
        return File.Exists(python) && File.Exists(entry) && Directory.Exists(profile)
            ? new(python!, entry!, profile!) : null;
    }

    internal async Task<KanbanState> CallAsync(object request)
    {
        var command = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-I", "-B", entry }) command.ArgumentList.Add(arg);
        using var process = Process.Start(command) ?? throw new IOException("無法開啟 Hermes 看板。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync();
            throw new IOException("看板操作逾時。請重新整理後確認狀態。");
        }
        await errors;
        if (process.ExitCode != 0) throw new IOException("無法讀取 Hermes 看板。");
        var state = JsonSerializer.Deserialize<KanbanState>(await output, Json);
        if (state?.Error is { } error) throw new IOException(error);
        return state is { Cards: not null } ? state : throw new IOException("看板沒有回傳有效狀態。");
    }
}
