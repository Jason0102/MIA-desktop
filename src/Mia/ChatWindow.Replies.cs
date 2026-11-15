using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using System.Windows.Controls;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    // Each response owns its text and cancellation; only audible playback is shared.
    private sealed class ReplyPresentation
    {
        private readonly ChatWindow owner;
        private readonly string who;
        private readonly CancellationToken ct;
        private readonly Guid id = Guid.NewGuid();
        private readonly Channel<string> sentences = Channel.CreateUnbounded<string>();
        private readonly bool audio;
        private readonly bool readUrls;
        private readonly VoiceService voice;
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
        internal ReplyPresentation(ChatWindow owner, string who, CancellationToken ct, bool readUrls = false,
            VoiceService? voice = null, IReadOnlyList<TaskResult>? results = null)
        {
            this.owner = owner; this.who = who; this.ct = ct;
            this.readUrls = readUrls;
            this.voice = voice ?? owner._app.Voice;
            this.results = results;
            audio = !owner._app.Settings.Muted && !string.IsNullOrWhiteSpace(owner._app.Settings.VoiceId);
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
                if (!owner._app.IsExiting)
                {
                    // Poll recovery, speech cancellation and service failures must never lose the result.
                    if (!audioStarted || fallback) ShowDirect();
                    EnsureBody();
                    body!.Text = SpeechTags.Display(final);
                    AddSourceLinks(body, final);
                    owner._scroll.ScrollToEnd();
                }
                if (textAnimationStarted is long start)
                {
                    // Count the time already spent animating while text streamed in.
                    var remaining = TextAnimationDuration(animatedText) - Stopwatch.GetElapsedTime(start);
                    var tail = TimeSpan.FromMilliseconds(400) - Stopwatch.GetElapsedTime(lastTextAt);
                    if (tail > remaining) remaining = tail;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
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
            try
            {
                await foreach (string sentence in sentences.Reader.ReadAllAsync(ct))
                {
                    await owner._speechQueue.WaitAsync(ct);
                    try
                    {
                        if (owner._app.Settings.Muted) { fallback = true; ShowDirect(); continue; }
                        Task reveal = Task.CompletedTask;
                        try
                        {
                            await voice.SpeakAsync(sentence, ct,
                                segment =>
                                {
                                    audioStarted |= voice.IsSpeaking;
                                    reveal = RevealAfterAsync(reveal, segment);
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

        private async Task RevealAfterAsync(Task previous, string segment)
        {
            await previous;
            if (ct.IsCancellationRequested || owner._app.IsExiting) return;
            string display = SpeechTags.Display(segment);
            if (string.IsNullOrWhiteSpace(display)) return;
            EnsureBody();
            string prefix = revealed.ToString();
            int[] starts = StringInfo.ParseCombiningCharacters(display);
            try
            {
                for (int count = 1; count <= starts.Length; count = Math.Min(count + 3, starts.Length))
                {
                    if (owner._app.IsExiting || ct.IsCancellationRequested) return;
                    body!.Text = prefix + (count == starts.Length ? display : display[..starts[count]]);
                    owner._scroll.ScrollToEnd();
                    if (count == starts.Length) break;
                    await Task.Delay(30, ct);
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

    private Task PresentReplyAsync(string who, string text, CancellationToken ct, bool readUrls = false, TaskResult? result = null)
        => new ReplyPresentation(this, who, ct, readUrls, results: result is { Files.Length: > 0 } ? [result] : null).CompleteAsync(text);
}
