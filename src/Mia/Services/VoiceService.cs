using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mia.Services;

public sealed class VoiceService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string,CancellationToken,Task> _playback;
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _current;
    private MediaPlayer? _activePlayer;
    private string? _activeFile;
    private bool _disposed;
    private readonly bool _streamPlayback;
    public event Action<bool>? SpeakingChanged;
    public event Action<SpeechAnimation>? SpeechAnimationChanged;
    private SpeechAnimation _playbackAnimation = SpeechAnimation.Default;
    public bool IsSpeaking { get; private set; }
    public bool LastCloneRequiresVerification { get; private set; }

    public VoiceService(AppSettings settings, HttpMessageHandler? handler = null) : this(settings,handler,null) { }

    internal VoiceService(AppSettings settings,HttpMessageHandler? handler,Func<string,CancellationToken,Task>? playback)
    {
        _settings = settings;
        _playback=playback??PlayFileAsync;
        _streamPlayback = playback is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public async Task SpeakAsync(string text, CancellationToken ct, Action<string>? segmentStarted = null, bool readUrls = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_settings.Muted || !SpeechTags.HasWords(text)) return;
        RequireVoice();

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.CancelAfter(TimeSpan.FromMinutes(5));
        lock (_sync) { _current?.Cancel(); _current = session; }
        bool entered = false;
        try
        {
            await _playGate.WaitAsync(session.Token);
            entered = true;
            var chunks = SpeechTags.Segments(text).SelectMany(ChunkText).Where(SpeechTags.HasWords).ToArray();
            if (_streamPlayback)
            {
                foreach (string chunk in chunks)
                {
                    session.Token.ThrowIfCancellationRequested();
                    if (_settings.Muted) break;
                    if (!HasSpokenWords(chunk, readUrls))
                    {
                        segmentStarted?.Invoke(chunk);
                        continue;
                    }
                    await StreamSentenceAsync(SpeechTags.SpokenText(chunk, readUrls), SpeechAnimation.Resolve(chunk),
                        session.Token, () => segmentStarted?.Invoke(chunk));
                }
                return;
            }
            chunks = chunks.Where(chunk => HasSpokenWords(chunk, readUrls)).ToArray();
            Task<string>? pending = null;
            try
            {
                if(chunks.Length>0)pending=GenerateFileAsync(SpeechTags.SpokenText(chunks[0], readUrls),session.Token);
                for(int i=0;i<chunks.Length;i++)
                {
                    session.Token.ThrowIfCancellationRequested();
                    string file=await pending!;
                    pending=null;
                    try
                    {
                        if(_settings.Muted)break;
                        // At most one upcoming sentence is generated while the current one plays.
                        if(i+1<chunks.Length)pending=GenerateFileAsync(SpeechTags.SpokenText(chunks[i+1], readUrls),session.Token);
                        _playbackAnimation = SpeechAnimation.Resolve(chunks[i]);
                        // The injected file player follows the same start/end contract as PCM playback.
                        SpeechAnimationChanged?.Invoke(_playbackAnimation);
                        IsSpeaking = true;
                        SpeakingChanged?.Invoke(true);
                        try
                        {
                            segmentStarted?.Invoke(chunks[i]);
                            await _playback(file,session.Token);
                        }
                        finally
                        {
                            IsSpeaking = false;
                            SpeakingChanged?.Invoke(false);
                            SpeechAnimationChanged?.Invoke(SpeechAnimation.Default);
                        }
                    }
                    finally { TryDelete(file); }
                }
            }
            catch(TaskCanceledException) when(!session.IsCancellationRequested)
            { throw new TimeoutException("語音服務逾時，文字回答仍可閱讀。"); }
            finally
            {
                session.Cancel();
                if(pending!=null)
                {
                    try { TryDelete(await pending); }
                    catch { /* Observe cancelled/failed prefetch; the active error takes priority. */ }
                }
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested && !session.IsCancellationRequested)
        { throw new TimeoutException("語音服務逾時，文字回答仍可閱讀。"); }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_current, session)) _current = null; }
            if (entered) _playGate.Release();
        }
    }

    public async Task SpeakStreamAsync(System.Threading.Channels.ChannelReader<string> sentences, CancellationToken ct, Action<string>? segmentStarted = null, bool readUrls = false)
    {
        await foreach (string sentence in sentences.ReadAllAsync(ct))
            await SpeakAsync(sentence, ct, segmentStarted, readUrls);
    }

    private static bool HasSpokenWords(string text, bool readUrls) =>
        SpeechTags.HasWords(SpeechTags.SpokenText(SpeechTags.Display(text), readUrls));

    private async Task StreamSentenceAsync(string text, SpeechAnimation animation, CancellationToken ct, Action started)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.elevenlabs.io/v1/text-to-dialogue/stream?output_format=pcm_24000");
        request.Headers.Add("xi-api-key", _settings.ElevenLabsKey.Trim());
        request.Content = JsonContent.Create(new { model_id = _settings.ElevenLabsModel, inputs = new[] { new { text, voice_id = _settings.VoiceId } } });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw ServiceErrors.ForStatus("ElevenLabs", response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType is string mime && !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && mime != "application/octet-stream")
            throw new IOException("語音服務未回傳音訊。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            await PcmStreamPlayer.PlayAsync(stream, () => OnDispatcherAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                SpeechAnimationChanged?.Invoke(animation);
                IsSpeaking = true;
                SpeakingChanged?.Invoke(true);
                started();
            }), ct);
        }
        finally
        {
            await OnDispatcherAsync(() => { IsSpeaking = false; SpeakingChanged?.Invoke(false); SpeechAnimationChanged?.Invoke(SpeechAnimation.Default); });
        }
    }

    private Task OnDispatcherAsync(Action action)
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task;
        action();
        return Task.CompletedTask;
    }

    private void RequireVoice()
    {
        RequireKey();
        if (string.IsNullOrWhiteSpace(_settings.VoiceId))
            throw new InvalidOperationException("請先在米亞設定 Voice ID。");
        if (_settings.ElevenLabsModel != "eleven_v4")
            throw new InvalidOperationException("此版本語音指定使用 eleven_v4，請檢查模型設定。");
    }

    internal async Task ExportAsync(string text, string destination, CancellationToken ct)
    {
        RequireVoice();
        string spoken = SpeechTags.SpokenText(text.Trim(), readUrls: true);
        if (spoken.Length > 5000 || !HasSpokenWords(text, readUrls: true))
            throw new ArgumentException("請提供 1 至 5000 字的語音稿。");
        string temporary = await GenerateFileAsync(spoken, ct);
        try { File.Move(temporary, destination); }
        finally { TryDelete(temporary); }
    }

    private async Task<string> GenerateFileAsync(string chunk,CancellationToken ct)
    {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.elevenlabs.io/v1/text-to-dialogue?output_format=mp3_44100_128");
                request.Headers.Add("xi-api-key", _settings.ElevenLabsKey.Trim());
                request.Content = JsonContent.Create(new
                {
                    model_id = _settings.ElevenLabsModel,
                    inputs = new[] { new { text = chunk, voice_id = _settings.VoiceId } }
                });
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) throw ServiceErrors.ForStatus("ElevenLabs", response.StatusCode);
                if (response.Content.Headers.ContentType?.MediaType is string mime && !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    && mime != "application/octet-stream")
                    throw new InvalidDataException("語音服務未回傳音訊，請檢查聲線與模型設定。");

                var directory = Path.Combine(SettingsStore.DataDirectory, "audio");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp3");
                try
                {
                    await using (var stream = await response.Content.ReadAsStreamAsync(ct))
                    await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int count, total = 0;
                        while ((count = await stream.ReadAsync(buffer.AsMemory(), ct)) != 0)
                        {
                            total += count;
                            if (total > 25 * 1024 * 1024) throw new InvalidDataException("回傳語音超出大小限制。");
                            await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        }
                        if (total == 0) throw new InvalidDataException("語音服務回傳空白音訊。");
                    }
                    return file;
                }
                catch { TryDelete(file); throw; }
    }

    private async Task PlayFileAsync(string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? player = null;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                player = new MediaPlayer();
                _activePlayer = player;
                _activeFile = file;
                player.MediaOpened += (_, _) =>
                {
                    if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
                    IsSpeaking = true;
                    SpeechAnimationChanged?.Invoke(_playbackAnimation);
                    SpeakingChanged?.Invoke(true);
                    player.Play();
                };
                player.MediaEnded += (_, _) => completion.TrySetResult();
                player.MediaFailed += (_, _) => completion.TrySetException(new InvalidOperationException("Windows 無法播放回傳的語音。"));
                player.Volume = 0.85;
                player.Open(new Uri(file));
            });
            await completion.Task.WaitAsync(ct);
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    player?.Stop(); player?.Close();
                    if (ReferenceEquals(_activePlayer, player)) { _activePlayer = null; _activeFile = null; }
                    IsSpeaking = false;
                    SpeakingChanged?.Invoke(false);
                    SpeechAnimationChanged?.Invoke(SpeechAnimation.Default);
                });
            }
        }
    }

    public void Stop() { lock (_sync) { _current?.Cancel(); } }

    public static IReadOnlyList<string> FindKiriMaterials(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("找不到語音素材資料夾，請重新選擇 voice/materials。");
        var root = Path.GetFullPath(folder);
        // Do not follow symbolic links/reparse points to another source directory.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("請選擇實際素材資料夾，不要選擇連結。");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileName(p).StartsWith("kiri_", StringComparison.OrdinalIgnoreCase)
                     && Path.GetExtension(p).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("資料夾中沒有 kiri_*.mp3 素材。");
        if (files.Length > 25) throw new InvalidOperationException("一次最多使用 25 個 kiri_* 素材。");
        long total = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("語音素材不能是連結檔案。");
            if (info.Length == 0 || info.Length > 10 * 1024 * 1024)
                throw new InvalidOperationException("每個 kiri_* 素材須大於 0 且不超過 10 MB。");
            total += info.Length;
        }
        if (total > 30 * 1024 * 1024) throw new InvalidOperationException("本次素材總大小不能超過 30 MB。");
        return files;
    }

    /// <summary>Called only after the UI shows the selected files and obtains voice rights/upload consent.</summary>
    public async Task<string> CloneKiriAsync(string folder, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireKey();
        var files = FindKiriMaterials(folder);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("MIA Kiri"), "name");
        form.Add(new StringContent("MIA desktop companion; exclusively user-selected kiri_* materials."), "description");
        foreach (var file in files)
        {
            var content = new StreamContent(File.OpenRead(file));
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(content, "files", Path.GetFileName(file));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/voices/add");
        request.Headers.Add("xi-api-key", _settings.ElevenLabsKey.Trim());
        request.Content = form;
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw ServiceErrors.ForStatus("ElevenLabs", response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (!root.TryGetProperty("voice_id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("聲線建立回覆缺少 Voice ID。");
        LastCloneRequiresVerification = root.TryGetProperty("requires_verification", out var verification) && verification.ValueKind == JsonValueKind.True;
        return id.GetString()!;
    }

    public static IReadOnlyList<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        string remaining=text.Trim();
        while(remaining.Length>0)
        {
            int take=Math.Min(1000,remaining.Length);
            for(int i=0;i<take;i++)
            {
                char c=remaining[i];
                var url = System.Text.RegularExpressions.Regex.Match(remaining[i..], @"^(?i:(?:https?://|www\.)[^\s<>，。！？；）]+|(?:[a-z0-9-]+\.)+(?:com|org|net|edu|gov|io|ai|tw)(?:/[^\s<>，。！？；）]*)?)");
                if (url.Success) { i += url.Length - 1; continue; }
                if (c == '[' && remaining.IndexOf(']', i + 1) is int close && close >= 0)
                {
                    i = close;
                    continue;
                }
                bool decimalPoint=c=='.' && i>0 && i+1<remaining.Length && char.IsDigit(remaining[i-1]) && char.IsDigit(remaining[i+1]);
                if(!decimalPoint && "。？！.?!\n".Contains(c))
                {
                    take=i+1;
                    while(take<remaining.Length && take<1000 && "。？！.?!\"'」』）)".Contains(remaining[take]))take++;
                    break;
                }
            }
            // Keep a bracketed direction intact at the maximum chunk boundary.
            int open = remaining.LastIndexOf('[', take - 1, take);
            if (open > 0 && remaining.IndexOf(']', open) >= take) take = open;
            if(take<remaining.Length && char.IsHighSurrogate(remaining[take-1]))take--;
            var chunk=remaining[..take];
            if(!string.IsNullOrWhiteSpace(chunk))chunks.Add(chunk);
            remaining=remaining[take..];
        }
        return chunks;
    }

    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.ElevenLabsKey))
            throw new InvalidOperationException("請先在設定中輸入 ElevenLabs API Key。");
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        if (_dispatcher.CheckAccess())
        {
            _activePlayer?.Stop(); _activePlayer?.Close();
            _activePlayer = null;
            if (_activeFile is not null) TryDelete(_activeFile);
            _activeFile = null;
            IsSpeaking = false;
        }
        _http.Dispose();
    }
}
