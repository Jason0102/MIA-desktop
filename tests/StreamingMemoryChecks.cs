using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Mia.Services;

internal static class StreamingMemoryChecks
{
    public static async Task RunAsync()
    {
        Check(Mia.SpeechAnimation.Resolve(SpeechTags.SpokenText("[audio: sad, softly]我有點難過。")).Tag == "sad", "same normalized text drives both ElevenLabs and animation");
        foreach (var animation in Mia.SpeechAnimation.All)
        {
            Check(Mia.SpeechAnimation.Resolve("[" + animation.Tag + "]台詞") == animation, "every named emotion resolves to its choreography");
            Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Interactions", animation.Gesture + ".png")), "emotion uses bundled original artwork");
            var motion = animation.Motion(animation.Period / 4);
            Check(double.IsFinite(motion.Tilt) && motion.ScaleY > .95 && motion.ScaleY < 1.05, "emotion motion remains bounded");
        }
        Check(Mia.SpeechAnimation.Resolve("沒有標籤") == Mia.SpeechAnimation.Default && Mia.SpeechAnimation.Resolve("[unrecognised delivery]") == Mia.SpeechAnimation.Default, "untagged and unknown speech use original animation");
        Check(Mia.SpeechAnimation.Resolve("[whispering, playful]").Tag == "playful", "compound cue prefers emotion over volume");
        Check(Mia.SpeechAnimation.Resolve("[nervous]").Tag == "anxious", "emotion synonyms resolve");
        Check(SpeechTags.Segments("[audio: happy]太好了，[audio: sad]但有點可惜。").SequenceEqual(new[] { "[audio: happy]太好了，", "[audio: sad]但有點可惜。" }), "mid-sentence changes get separate audio segments");
        Check(SpeechTags.Segments("[audio: softly][audio: sad]你好。").Single() == "[audio: softly][audio: sad]你好。", "adjacent directions stay with their words");
        Check(SpeechTags.Display("[happy]太好了！[curious] 接下來呢？") == "太好了！接下來呢？", "speech tags hidden from chat");
        string tagged = "[whispers]晚安。";
        for (int i = 1; i < "[whispers]".Length; i++)
            Check(SpeechTags.Display(tagged[..i], true) == "", "partial streaming tags never flash");
        Check(SpeechTags.Display("陣列 [0] 和 [文件](https://example.org)") == "陣列 [0] 和 [文件](https://example.org)", "ordinary brackets preserved");
        Check(SpeechTags.Display("[cur", false) == "[cur", "incomplete final text preserved");
        var boundary = new string('字', 995) + "[happy]好了。";
        var split = VoiceService.ChunkText(boundary);
        Check(string.Concat(split) == boundary && split[1].StartsWith("[happy]"), "chunk limit never tears a speech tag");
        await CheckVoiceQueueAsync();
        var settings = new AppSettings { OpenAiKey = "synthetic" };
        var store = new ConversationStore(); store.Clear();
        for (int i = 0; i < 12; i++) store.Append("user-" + i, "answer-" + i);
        using (var restored = new AiService(settings))
        {
            Check(restored.History.Count == 20 && restored.History[0].Text == "user-2", "restart restores latest ten complete turns");
        }
        File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "conversation.jsonl"), "{torn");
        store.Append("after-crash", "recovered");
        Check(store.LoadRecent()[^1].User == "after-crash", "torn record does not corrupt subsequent saves");
        store.Clear();
        bool deltaSeen = false;
        string completed = """{"output":[{"type":"message","content":[{"type":"output_text","text":"第一句。第二句。","annotations":[{"type":"url_citation","url":"https://example.org/source","title":"Source"}]}]}],"usage":{"input_tokens_details":{"cached_tokens":1024}}}""";
        string sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"第一句。\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"第二句。\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":" + completed + "}\n\n";
        using (var ai = new AiService(settings, new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(json.RootElement.GetProperty("stream").GetBoolean(), "SSE requested");
            Check(json.RootElement.GetProperty("instructions").GetString()!.Contains(SpeechTags.Instructions), "emotion instructions reach GPT");
            Check(json.RootElement.GetProperty("instructions").GetString()!.Contains("貓精靈桌面夥伴"), "public role reaches outgoing request");
            Check(json.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!.Contains("本次發話的使用者本地時間"), "local clock reaches outgoing request");
            Check(json.RootElement.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search", "web search request contract");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        })))
        {
            string reply = await ai.ReplyWithImagesAsync("查詢", [], CancellationToken.None, delta =>
            {
                Check(ai.History.Count == 0, "text available before completion and persistence"); deltaSeen = true;
            });
            Check(deltaSeen && reply.Contains("https://example.org/source") && ai.LastCachedTokens == 1024, "stream text, citations and cached usage parsed");
        }
        using (var restarted = new AiService(settings))
            Check(restarted.History.Count == 2, "completed stream survives restart");
        using (var broken = new AiService(settings, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n", Encoding.UTF8, "text/event-stream") }))))
        {
            try { await broken.ReplyWithImagesAsync("interrupted", [], CancellationToken.None, _ => { }); throw new Exception("Expected interruption"); }
            catch (IOException) { }
            Check(store.LoadRecent().Count == 1, "incomplete stream never persisted");
            broken.ClearHistory();
        }
        Check(!File.Exists(Path.Combine(SettingsStore.DataDirectory, "conversation.jsonl")), "clear removes disk memory");
        Check(new[] { "你好", "天氣如何", new string('字', 130), "分析", "嚴格證明", "最高推理" }
            .Select(t => AiService.SelectReasoning(t, 0)).SequenceEqual(new[] { "none", "low", "medium", "high", "xhigh", "max" }), "automatic router can use all six reasoning levels");
        Console.WriteLine("PASS streaming completion/interruption, persistent ten-turn memory, citations, prompt cache and six-level reasoning");
    }
    private static async Task CheckVoiceQueueAsync()
    {
        var played = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctor = typeof(VoiceService).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        using var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString()!)) };
        });
        Func<string, CancellationToken, Task> playback = async (file, ct) => { played.Add(await File.ReadAllTextAsync(file, ct)); first.TrySetResult(); };
        using var voice = (VoiceService)ctor.Invoke(new object[] { new AppSettings { ElevenLabsKey = "synthetic", VoiceId = "synthetic" }, handler, playback });
        var queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = voice.SpeakStreamAsync(queue.Reader, timeout.Token);
        queue.Writer.TryWrite("[happy]第一句。");
        await first.Task.WaitAsync(timeout.Token);
        Check(!task.IsCompleted, "first sentence plays while text stream remains open");
        queue.Writer.TryWrite("[audio: whispering, playful]第二句。"); queue.Writer.TryWrite("[audio: clears throat]"); queue.Writer.Complete();
        await task;
        Check(played.SequenceEqual(new[] { "[happy]第一句。", "[whispering, playful]第二句。" }), "legacy and open-ended cues reach ElevenLabs; tag-only output skipped");
        await voice.SpeakAsync("[sad]第一句。[happy]第二句。", timeout.Token);
        Check(played.TakeLast(2).SequenceEqual(new[] { "[sad]第一句。", "[happy]第二句。" }), "replay preserves per-sentence emotions");
        await voice.SpeakAsync("[audio: excited]成功了，[audio: thoughtful]讓我想想。", timeout.Token);
        Check(played.TakeLast(2).SequenceEqual(new[] { "[excited]成功了，", "[thoughtful]讓我想想。" }), "mid-sentence emotions are generated and played separately");
        var waiting = System.Threading.Channels.Channel.CreateUnbounded<string>();
        using var cancel = new CancellationTokenSource();
        var canceled = voice.SpeakStreamAsync(waiting.Reader, cancel.Token); cancel.Cancel();
        try { await canceled; throw new Exception("Expected voice queue cancellation"); } catch (OperationCanceledException) { }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}
