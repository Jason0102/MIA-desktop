using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mia.Services;

namespace Mia;

/// <summary>Serves the desktop user's dashboard on this machine while MIA is running.</summary>
internal sealed partial class InteractionDashboard : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxFormBytes = 4096;
    private readonly InteractionMemory _memory;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(4, 4);
    private readonly string _path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _authority;
    internal Uri Address { get; }

    internal InteractionDashboard(InteractionMemory memory)
    {
        _memory = memory;
        _listener.Start(16);
        _authority = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        Address = new Uri($"http://{_authority}{_path}");
        _ = ListenAsync(_stop.Token);
    }

    internal void Open() => Process.Start(new ProcessStartInfo(Address.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task ListenAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await _connections.WaitAsync(stop).ConfigureAwait(false);
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
                    _ = HandleAsync(client, stop);
                }
                catch
                {
                    _connections.Release();
                    throw;
                }
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested &&
            ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var stream = client.GetStream();
                await ServeAsync(stream, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        finally { _connections.Release(); }
    }

    private async Task ServeAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        try
        {
            var header = await ReadHeaderAsync(reader, ct).ConfigureAwait(false);
            var request = header[0].Split(' ');
            if (request.Length != 3 || request[2] != "HTTP/1.1")
                throw new InvalidDataException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in header.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                    throw new InvalidDataException();
            }
            string[] parts = request[1].Split('?', 2);
            if (headers.GetValueOrDefault("Host") != _authority || parts[0] != _path)
            {
                await SendAsync(stream, "404 Not Found", "找不到頁面。", ct).ConfigureAwait(false);
                return;
            }
            var query = DecodeForm(parts.Length == 2 ? parts[1] : "");
            var filters = DashboardFilters.Parse(query);
            switch (request[0])
            {
                case "GET":
                    await SendAsync(stream, "200 OK", Render(_memory, filters, _path, query.GetValueOrDefault("notice")), ct)
                        .ConfigureAwait(false);
                    break;
                case "POST":
                    await PostAsync(stream, reader, headers, filters, ct).ConfigureAwait(false);
                    break;
                default:
                    await SendAsync(stream, "405 Method Not Allowed", "不支援這個方法。", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or ArgumentException)
        {
            await SendAsync(stream, "400 Bad Request", "請檢查輸入內容後再試一次。", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await SendAsync(stream, "409 Conflict", "記憶可能正在重置，請從米亞設定重新開啟後台。", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SocketException)
        {
            Trace.TraceWarning("MIA dashboard request failed: {0}", ex.GetType().Name);
            await SendAsync(stream, "500 Internal Server Error", "讀寫互動記憶失敗，請返回重試。", ct).ConfigureAwait(false);
        }
    }

    private async Task PostAsync(NetworkStream stream, StreamReader reader, Dictionary<string, string> headers,
        DashboardFilters filters, CancellationToken ct)
    {
        if (headers.GetValueOrDefault("Origin") != $"http://{_authority}" ||
            headers.ContainsKey("Transfer-Encoding") ||
            headers.GetValueOrDefault("Content-Type")?.Split(';')[0] != "application/x-www-form-urlencoded" ||
            !int.TryParse(headers.GetValueOrDefault("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
            length is < 1 or > MaxFormBytes)
            throw new InvalidDataException();

        var buffer = new char[length];
        if (await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false) != length ||
            buffer.Any(ch => ch > 127))
            throw new InvalidDataException();
        var form = DecodeForm(new string(buffer));
        string generation = Required(form, "generation", 32);
        string notice;
        switch (form.GetValueOrDefault("action"))
        {
            case "configure":
                if (!int.TryParse(form.GetValueOrDefault("days"), out int days) || days is < 1 or > 365)
                    throw new InvalidDataException();
                _memory.Configure(form.GetValueOrDefault("enabled") == "on", days, generation);
                notice = "saved";
                break;
            case "exclude":
                _memory.ExcludeTopic(Required(form, "topic", 80), generation);
                notice = "excluded";
                break;
            case "rename":
                _memory.RenameTopic(Required(form, "topic", 80), Required(form, "replacement", 80), generation);
                notice = "renamed";
                break;
            case "preference":
                string name = Required(form, "name", 40);
                string value = form.GetValueOrDefault("value", "").Trim();
                if (value.Length > 300) throw new InvalidDataException();
                _memory.SetPreference(name, value, generation);
                notice = value.Length == 0 ? "removed" : "saved";
                break;
            default:
                throw new InvalidDataException();
        }
        await SendAsync(stream, "303 See Other", "", ct, _path + filters.Query + "&notice=" + notice)
            .ConfigureAwait(false);
    }

    private static string Required(Dictionary<string, string> form, string field, int max)
    {
        var value = form.GetValueOrDefault(field, "").Trim();
        if (value.Length == 0 || value.Length > max) throw new InvalidDataException();
        return value;
    }

    private static async Task<IReadOnlyList<string>> ReadHeaderAsync(StreamReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        var tail = new StringBuilder();
        var single = new char[1];
        for (int read = 0; read < MaxHeaderBytes; read++)
        {
            if (await reader.ReadAsync(single.AsMemory(), ct).ConfigureAwait(false) != 1)
                break;
            char character = single[0];
            if (character == '\n')
            {
                if (tail.Length == 0 || tail[^1] != '\r') break;
                if (tail.Length == 1 && lines.Count > 0) return lines;
                lines.Add(tail.ToString(0, tail.Length - 1));
                tail.Clear();
            }
            else if (character is >= ' ' and <= '~' or '\r' or '\t')
                tail.Append(character);
            else
                break;
        }
        throw new InvalidDataException();
    }

    private static Dictionary<string, string> DecodeForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = field.Split('=', 2);
            if (parts.Length != 2 || !result.TryAdd(WebUtility.UrlDecode(parts[0]), WebUtility.UrlDecode(parts[1])))
                throw new InvalidDataException();
        }
        return result;
    }

    private static async Task SendAsync(NetworkStream stream, string status, string body,
        CancellationToken ct, string? location = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string type = status == "200 OK" ? "text/html" : "text/plain";
        string response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: {type}; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\nReferrer-Policy: same-origin\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'\r\n" +
            (location == null ? "" : $"Location: {location}\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        if (bytes.Length > 0) await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
}
