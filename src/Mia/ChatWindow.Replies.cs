using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using System.Windows.Controls;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private CancellationTokenSource _speechInterruption = new();

    private void InterruptReplyAudio()
    {
        var previous = _speechInterruption;
        _speechInterruption = new();
        previous.Cancel();
        previous.Dispose();
        _app.Voice.Stop();
    }

    // Each response owns its text and cancellation; only audible playback is shared.
    private sealed class ReplyPresentation
    {
        private readonly ChatWindow owner;
        private readonly string who;
        private readonly CancellationToken ct;
        private readonly CancellationToken speechGeneration;
        private readonly Guid id = Guid.NewGuid();
        private readonly Channel<string> sentences = Channel.CreateUnbounded<string>();
        private readonly bool audio;
        private readonly bool readUrls;
        private readonly VoiceService voice;
        private readonly InteractionMemory? memory;
        private int spokenSegments;
        private IReadOnlyList<TaskResult>? results;
        private readonly Task playback;
        private readonly StringBuilder pending = new();
        private readonly StringBuilder revealed = new();
        private TextBox? body;
        private string text = "";
        private bool finished, fallback, audioStarted;
        private long? textAnimationStarted;
        private long lastTextAt;
        private string animatedText = "";

        internal string VisibleText => body?.Text ?? "";
        internal string? VoiceError { get; private set; }
        internal MoodSnapshot Mood { get; }
        internal ReplyPresentation(ChatWindow owner, string who, CancellationToken ct, bool readUrls = false,
            VoiceService? voice = null, IReadOnlyList<TaskResult>? results = null, MoodSnapshot? mood = null)
        {
            this.owner = owner; this.who = who; this.ct = ct;
            speechGeneration = owner._speechInterruption.Token;
            this.readUrls = readUrls;
            this.voice = voice ?? owner._app.Voice;
            this.results = results;
            memory = InteractionMemory.Desktop();
            Mood = mood ?? InteractionMood.FromMemory(memory);
            audio = !owner._app.Settings.Muted;
            playback = audio ? PlayAsync() : Task.CompletedTask;
        }

        internal void Update(string value)
        {
            if (finished || ct.IsCancellationRequested || owner._app.IsExiting || value == text) return;
            string delta = value.StartsWith(text, StringComparison.Ordinal) ? value[text.Length..] : "";
            text = value;
            if (!audio || fallback || owner._app.Settings.Muted) { ShowDirect(); return; }
            pending.Append(delta);
            string buffer = pending.ToString().TrimStart();
            var chunks = VoiceService.ChunkText(buffer);
            int ready = chunks.Count - 1;
            if (chunks.Count > 0 && ("。？！!?\n".Contains(chunks[^1][^1]) || chunks[^1].Length >= 1000)) ready++;
            int consumed = 0;
            for (int i = 0; i < ready; i++)
            {
                sentences.Writer.TryWrite(chunks[i]);
                consumed = buffer.IndexOf(chunks[i], consumed, StringComparison.Ordinal) + chunks[i].Length;
            }
            pending.Clear(); pending.Append(buffer[consumed..]);
        }

        private void ShowDirect()
        {
            string display = SpeechTags.Display(text, streaming: !finished);
            if (string.IsNullOrWhiteSpace(display)) return;
            EnsureBody();
            body!.Text = display;
            owner._scroll.ScrollToEnd();
            if (speechGeneration.IsCancellationRequested) return;
            if (display == animatedText) return;
            owner._app.Pet.SetTextReply(id, text);
            lastTextAt = Stopwatch.GetTimestamp();
            textAnimationStarted ??= lastTextAt;
            animatedText = display;
        }

        internal async Task CompleteAsync(string final, string? spokenFinal = null)
        {
            Update(spokenFinal ?? final);
            finished = true;
            if (audio && !fallback && !owner._app.Settings.Muted && pending.Length > 0)
                sentences.Writer.TryWrite(pending.ToString());
            sentences.Writer.TryComplete();
            try
            {
                await playback;
                ct.ThrowIfCancellationRequested();
                if (!owner._app.IsExiting)
                {
                    // Poll recovery, speech cancellation and service failures must never lose the result.
                    if (!audioStarted || fallback) ShowDirect();
                    EnsureBody();
                    body!.Text = SpeechTags.Display(final);
                    AddSourceLinks(body, final);
                    if (VoiceError is { } note &&
                        body.Parent is StackPanel panel)
                        panel.Children.Add(Ui.Text(note, 12, Ui.Subtle));
                    owner._scroll.ScrollToEnd();
                    if (!ct.IsCancellationRequested)
                    {
                        int index = spokenSegments;
                        foreach (string part in VoiceService.ChunkText(text).SelectMany(SpeechTags.Segments).Skip(index))
                            Trace(part, "text", index++);
                    }
                }
                if (!speechGeneration.IsCancellationRequested && textAnimationStarted is long start)
                {
                    // Count the time already spent animating while text streamed in.
                    var remaining = TextAnimationDuration(animatedText) - Stopwatch.GetElapsedTime(start);
                    var tail = TimeSpan.FromMilliseconds(400) - Stopwatch.GetElapsedTime(lastTextAt);
                    if (tail > remaining) remaining = tail;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var animation = CancellationTokenSource.CreateLinkedTokenSource(ct, speechGeneration);
                        try { await Task.Delay(remaining, animation.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    }
                }
            }
            finally { owner._app.Pet.SetTextReply(id, null); }
        }

        private void EnsureBody()
        {
            if (body is not null) return;
            body = owner.AddMessage(who, "", true);
            if (results is { } available) AddResults(available);
        }

        internal void SetResults(IReadOnlyList<TaskResult> available)
        {
            results = available;
            if (body is not null) AddResults(available);
        }

        internal void AttachActions(StackPanel actions)
        {
            EnsureBody();
            if (body?.Parent is StackPanel panel) panel.Children.Add(actions);
        }

        private void AddResults(IReadOnlyList<TaskResult> available)
        {
            if (body?.Parent is not StackPanel panel) return;
            foreach (var result in available)
                panel.Children.Add(owner.BuildResultCard(result, full: available.Count > 1 || result.Files.Length == 0));
        }

        internal async Task AbortAsync()
        {
            finished = true;
            sentences.Writer.TryComplete();
            await playback;
            owner._app.Pet.SetTextReply(id, null);
        }

        private async Task PlayAsync()
        {
            using var speech = CancellationTokenSource.CreateLinkedTokenSource(ct, speechGeneration);
            var audioToken = speech.Token;
            try
            {
                await foreach (string sentence in sentences.Reader.ReadAllAsync(audioToken))
                {
                    await owner._speechQueue.WaitAsync(audioToken);
                    try
                    {
                        if (owner._app.Settings.Muted) { fallback = true; ShowDirect(); continue; }
                        Task reveal = Task.CompletedTask;
                        try
                        {
                            await voice.SpeakAsync(sentence, audioToken,
                                segment =>
                                {
                                    audioStarted |= voice.IsSpeaking;
                                    if (voice.IsSpeaking) Trace(segment, "audio", spokenSegments++);
                                    reveal = RevealAfterAsync(reveal, segment, audioToken);
                                }, readUrls);
                        }
                        finally { await reveal; }
                    }
                    finally { owner._speechQueue.Release(); }
                }
            }
            catch (OperationCanceledException) { fallback = true; if (!ct.IsCancellationRequested) ShowDirect(); }
            catch (Exception ex)
            {
                fallback = true;
                VoiceError = "語音未完成，已顯示文字。" + Friendly(ex);
                if (!owner._app.IsExiting) { ShowDirect(); owner._status.Text = VoiceError; }
            }
        }

        private void Trace(string segment, string mode, int index)
        {
            if (memory is null || string.IsNullOrEmpty(Mood.MemoryGeneration)) return;
            string tag = SpeechAnimation.Resolve(segment).Tag;
            if (tag.Length == 0) tag = "peaceful";
            var delivery = SpeechTags.DeliveryDirections(segment);
            string voiceCues = delivery.Count == 0 ? "無" : string.Join('、', delivery);
            string group = id.ToString("N");
            string presentation = mode == "audio" ? "開始播放" : "以文字呈現";
            string detail = $"第 {index + 1} 段 · {presentation} · 情緒／動畫 {tag} · 語音指示 {voiceCues} · 心情 {Mood.State}（{string.Join('、', Mood.Reasons)}）";
            memory.Record(new InteractionEvent($"expression-{group}-{index}", DateTimeOffset.UtcNow, "PC", "expression",
                Group: group, Status: mode, Detail: detail), Mood.MemoryGeneration);
        }

        private async Task RevealAfterAsync(Task previous, string segment, CancellationToken audioToken)
        {
            await previous;
            if (audioToken.IsCancellationRequested || owner._app.IsExiting) return;
            string display = SpeechTags.Display(segment);
            if (string.IsNullOrWhiteSpace(display)) return;
            EnsureBody();
            string prefix = revealed.ToString();
            int[] starts = StringInfo.ParseCombiningCharacters(display);
            try
            {
                for (int count = 1; count <= starts.Length; count = Math.Min(count + 3, starts.Length))
                {
                    if (owner._app.IsExiting || audioToken.IsCancellationRequested) return;
                    body!.Text = prefix + (count == starts.Length ? display : display[..starts[count]]);
                    owner._scroll.ScrollToEnd();
                    if (count == starts.Length) break;
                    await Task.Delay(30, audioToken);
                }
                revealed.Append(display);
            }
            catch (OperationCanceledException) { }
        }
    }

    private static TimeSpan TextAnimationDuration(string text)
    {
        // The 43 Mandarin EMO samples (000–042) have a median rate of 3.96 non-punctuation characters/s.
        const double MandarinCharactersPerSecond = 3.96;
        const double LatinCharactersPerSecond = 15; // Estimate for text outside the Mandarin sample set.
        double seconds = 0;
        foreach (int index in StringInfo.ParseCombiningCharacters(text))
        {
            char c = text[index];
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c)) continue;
            seconds += 1 / (char.IsAsciiLetterOrDigit(c) ? LatinCharactersPerSecond : MandarinCharactersPerSecond);
        }
        return TimeSpan.FromSeconds(Math.Max(1.2, seconds));
    }

    private Task PresentReplyAsync(string who, string text, CancellationToken ct, bool readUrls = false,
        TaskResult? result = null, MoodSnapshot? mood = null)
        => new ReplyPresentation(this, who, ct, readUrls, results: result is { Files.Length: > 0 } ? [result] : null,
            mood: mood).CompleteAsync(text);
}
