using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Mia;
using Mia.Services;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--preview-audio") return PreviewAudio.RunAsync(args[1]).GetAwaiter().GetResult();
        if (args.Contains("--setup-voice")) return VoiceSetup.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--live")) return LiveProbe.RunAsync().GetAwaiter().GetResult();
        // All credentials are synthetic; every cloud request uses an in-memory handler.
        var scratch = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("MIA_DATA_DIR", Path.Combine(scratch, "settings"));
        try
        {
            foreach (string name in new[] { "附件.docx", "資料.csv", "未知.custom", "無副檔名", "program.exe" })
            {
                string path = Path.Combine(scratch, name);
                File.WriteAllText(path, "attachment fixture; never executed");
                Check(FileAttachments.Validate(path) == Path.GetFullPath(path), "accept task attachment " + name);
                File.Delete(path);
                try { FileAttachments.Validate(path); Check(false, "missing attachment rejected"); }
                catch (IOException) { Check(true, "missing attachment rejected before send"); }
            }
            TestCaptureSizing();
            TestCaptureCancellation();
            TestSprites();
            TestSettings();
            AttentionChecks.Run(Check);
            ScreenChecks.Run(Check);
            WindowBallChecks.Run(Check);
            TimedCompanionChecks.Run(Check);
            Check(CompanionRules.CanKick(70,980,1000,192,208,false),"ground ball in foot range can be kicked");
            Check(!CompanionRules.CanKick(70,700,1000,192,208,false),"airborne ball cannot be kicked remotely");
            Check(!CompanionRules.CanKick(300,980,1000,192,208,false),"distant ball cannot be kicked");
            Check(!CompanionRules.CanKick(70,980,1000,192,208,true),"held ball cannot be kicked");
            Check(!BallPlayRules.ShouldMove("companion",false,false),"companion remains stationary without a ball");
            Check(BallPlayRules.ShouldMove("explore",false,false),"exploration moves without pointer following");
            Check(BallPlayRules.ShouldMove("companion",true,false)&&BallPlayRules.ShouldMove("explore",true,false),"ball play overrides either base mode");
            Check(!BallPlayRules.ShouldMove("explore",true,true),"user interaction pauses exploration and ball play");
            Check(BallPlayRules.NormalizeMode("play")=="explore" && BallPlayRules.NormalizeMode("focus")=="companion","legacy modes migrate to the two supported modes");
            Check(BallPlayRules.PredictHeadX(100,100,100,650,900,1000,20,0,4000,650)>BallPlayRules.PredictHeadX(100,100,100,650,900,1000,20,0,4000),"head interception respects terminal falling speed on tall screens");
            var lowThrow=BallPlayRules.ThrowVelocity(200,900,-.5,.4);var highThrow=BallPlayRules.ThrowVelocity(1000,900,.5,.4);
            Check(lowThrow.X<0 && highThrow.X>0 && highThrow.Y<lowThrow.Y,"random throw angle and screen headroom affect launch velocity");
            Check(highThrow.Y*highThrow.Y/1800<1000,"planned throw apex remains below top screen clearance");
            Check(BallPlayRules.ThrowVelocity(0,900,.5,1)==(0d,0d),"no headroom cannot launch ball through top border");
            Check(BallPlayRules.CanHead(90,110,100,10,192,200,false),"descending ball crossing head plane can be headed");
            Check(!BallPlayRules.CanHead(90,110,100,150,192,200,false)&&!BallPlayRules.CanHead(90,110,100,10,192,-200,false)&&!BallPlayRules.CanHead(90,110,100,10,192,200,true),"heading rejects distant ascending and held balls");
            Check(BallPlayRules.PredictHeadX(300,100,0,100,900,400,21,0,1000)==300,"vertical falling ball interception remains directly underneath");
            Check(LocomotionRules.Pose(80,192,"idle")=="walk-right" && LocomotionRules.Pose(-80,192,"idle")=="walk-left","slow movement selects directional walking");
            Check(LocomotionRules.Pose(180,192,"walk-right")=="right","fast ball chase retains original run");
            Check(LocomotionRules.Pose(240,576,"idle")=="walk-right" && LocomotionRules.Pose(40,96,"idle")=="walk-right","walk selection is independent of pet size and display DPI");
            Check(LocomotionRules.Pose(145,192,"walk-right")=="walk-right" && LocomotionRules.Pose(145,192,"right")=="right","walk run threshold has hysteresis");
            Check(LocomotionRules.Pose(0,192,"walk-left")=="idle","stopped movement does not keep a walking pose");
            Check(LocomotionRules.WalkFrame(0,8)==0 && LocomotionRules.WalkFrame(70,8)==4 && LocomotionRules.WalkFrame(140,8)==0,"walking steps loop by traveled distance");
            var clock=new FocusClock();clock.Start(1);clock.Tick(20,true);
            Check(ExplorationRules.MotionSeconds(0)>=8&&ExplorationRules.MotionSeconds(1)<=16,"exploration keeps each pace for eight to sixteen seconds");
            Check(ExplorationRules.SeatSeconds(true,0)==60&&ExplorationRules.SeatSeconds(false,1)<=60,"manual and autonomous exploration seats never exceed sixty seconds");
            Check(ExplorationRules.CanReach(1000,500,203,0,1040,500,192),"reachable window with standing headroom accepts a jump");
            Check(!ExplorationRules.CanReach(1000,180,203,0,1040,500,192)&&!ExplorationRules.CanReach(1000,500,203,0,1040,200,192),"jump rejects insufficient headroom and narrow ledges");
            Check(ExplorationRules.CanReach(900,-100,203,-1000,2000,500,192),"portrait and negative-coordinate monitors use actual work-area geometry");
            var leap=ExplorationRules.Jump(500,297,208);
            Check(leap.VerticalSpeed<0&&leap.FlightSeconds>0&&Math.Abs(leap.VerticalSpeed*leap.FlightSeconds+.5*ExplorationRules.Gravity*leap.FlightSeconds*leap.FlightSeconds+500)<.01,"window jump intersects target height on descending arc");
            var lowLeap=ExplorationRules.Jump(300,20,208);
            Check(lowLeap.VerticalSpeed*lowLeap.VerticalSpeed/(2*ExplorationRules.Gravity)<=312.01,"jump apex preserves screen-top clearance");
            Check(Enumerable.Range(0,5).All(i=>Enumerable.Range(0,5).All(j=>ExplorationRules.IdleChoice(i,j)!=i)),"five idle gestures never immediately repeat");
            Check(ExplorationRules.IdleFrame(0,4,2)==0&&ExplorationRules.IdleFrame(2,4,2)==1&&ExplorationRules.IdleFrame(3.9,4,2)==0,"idle gestures have anticipation contact and recovery phases");
            Check(clock.RemainingSeconds==60,"pause holds focus countdown");clock.Tick(59,false);
            Check(clock.Running&&!clock.Completed,"focus does not finish early");clock.Tick(2,false);
            Check(clock.Completed&&!clock.Running&&clock.RemainingSeconds==0,"focus completes once at zero");clock.Stop();
            Check(!clock.Completed,"focus reminder can be dismissed");
            Check(InteractionGeometry.MustLeavePerch(145,145,0,false), "head touching top detaches");
            Check(!InteractionGeometry.MustLeavePerch(146,145,0,false), "normal headroom keeps perch");
            Check(InteractionGeometry.MustLeavePerch(400,145,0,true), "maximized window detaches");
            Check(InteractionGeometry.MustLeavePerch(-900,145,-1000,false), "negative monitor coordinates detach correctly");
            var fling=new BallThrowTracker();fling.Reset(0,0,0);fling.Sample(60,-90,.1);
            var thrown=fling.Release(.11,1);
            Check(thrown.X>0 && thrown.Y<0,"drag release retains both horizontal and upward velocity");
            Check(fling.Release(.3,1)==(0d,0d),"holding ball still before release removes stale throw momentum");
            fling.Reset(0,0,1);Check(fling.Release(1.01,1)==(0d,0d),"click without drag drops without invented velocity");
            fling.Sample(-5000,5000,1.02);thrown=fling.Release(1.03,1);
            Check(thrown.X<0 && thrown.Y>0 && Math.Sqrt(thrown.X*thrown.X+thrown.Y*thrown.Y)<=1100.01,"fast diagonal throw preserves direction and limits speed");
            Check(Math.Abs(fling.Release(1.03,3).X)>Math.Abs(thrown.X),"throw speed cap scales with pet and monitor size");
            TestVoiceChunks();
            TestSentencePlaybackAsync().GetAwaiter().GetResult();
            InteractionChecks.Run(Check);
            BatchAndShakeChecks.Run(Check);
            TestCloudAsync(scratch).GetAwaiter().GetResult();
            StreamingMemoryChecks.RunAsync().GetAwaiter().GetResult();
            ServiceChecks.RunAsync().GetAwaiter().GetResult();
            HermesChecks.RunAsync(Check).GetAwaiter().GetResult();
            MemoryContinuityChecks.Run(Check);
            Console.WriteLine($"PASS: {passed} checks. No live API calls or desktop captures were made.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++;
        Console.WriteLine("  ok " + name);
    }

    private static void TestCaptureSizing()
    {
        var method = typeof(CaptureService).GetMethod("FitImage", BindingFlags.Static | BindingFlags.NonPublic)!;
        Size Fit(int width, int height) => (Size)method.Invoke(null, new object[] { width, height })!;
        Check(Fit(3840, 2160) == new Size(1600, 900), "4K captures preserve aspect ratio within 1600 px");
        Check(Fit(100, 200) == new Size(100, 200), "small captures are never upscaled");
        Check(Fit(1, 4000) == new Size(1, 1600), "thin captures remain nonzero");
        bool invalid = false;
        try { Fit(0, 200); } catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "invalid dimensions rejected");
    }

    private static void TestCaptureCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check(CaptureService.CaptureRegionAsync(cancelled.Token).GetAwaiter().GetResult() == null,
            "pre-cancelled region capture exits before screen access");
        Check(CaptureService.CaptureWindowAsync(cancelled.Token).GetAwaiter().GetResult() == null,
            "pre-cancelled window capture exits before enumeration or picker");

        // Cancel synchronously during the initial compositor delay, before any bitmap is acquired.
        var pending = CaptureService.CaptureRegionAsync();
        CaptureService.CancelActive();
        Check(pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() == null,
            "app exit cancels pending region capture before screen access");

        using var external = new CancellationTokenSource();
        var externallyCancelled = CaptureService.CaptureRegionAsync(external.Token);
        external.Cancel();
        Check(externallyCancelled.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() == null,
            "linked token cancels pending region capture before screen access");
        CaptureService.CancelActive();
        CaptureService.CancelActive();
        Check(true, "repeated capture cancellation after cleanup is harmless");
    }

    private static void TestSprites()
    {
        using var bitmap = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "spritesheet.png"));
        Check(bitmap.Width == 6144 && bitmap.Height == 9152, "HD sprite atlas contains 768 x 832 cells at four times logical resolution");
        int[] counts = { 6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8 };
        bool nonempty = true;
        for (int row = 0; row < counts.Length; row++)
        for (int col = 0; col < counts[row]; col++)
        {
            bool found = false;
            for (int y = 0; y < 832 && !found; y += 16)
            for (int x = 0; x < 768 && !found; x += 16)
                found = bitmap.GetPixel(col * 768 + x, row * 832 + y).A > 20;
            nonempty &= found;
        }
        Check(nonempty, "all 73 playable sprite cells contain visible pixels");
        Check(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(bitmap.Width-1, bitmap.Height-1).A == 0, "atlas outer corners retain transparency");
    }

    private static void TestSettings()
    {
        var value = new AppSettings { OpenAiKey = "fake-openai-secret-do-not-use", ElevenLabsKey = "fake-elevenlabs-secret-do-not-use", VoiceId = "fake-voice", Mode = "focus" };
        SettingsStore.Save(value);
        var json = File.ReadAllText(Path.Combine(SettingsStore.DataDirectory, "settings.json"));
        Check(!json.Contains(value.OpenAiKey) && !json.Contains(value.ElevenLabsKey), "stored settings never contain plaintext keys");
        var loaded = SettingsStore.Load();
        Check(loaded.OpenAiKey == value.OpenAiKey && loaded.ElevenLabsKey == value.ElevenLabsKey && loaded.Mode == "companion", "DPAPI settings round trip");
        value.PetScale=.5;value.UiScale=3;SettingsStore.Save(value);
        loaded=SettingsStore.Load();
        Check(loaded.PetScale==.5 && loaded.UiScale==3,"independent scale endpoints persist");
        value.AllowedScreenIds=new[]{"left","right"};SettingsStore.Save(value);loaded=SettingsStore.Load();
        Check(loaded.AllowedScreenIds!.SequenceEqual(value.AllowedScreenIds),"selected activity screens persist with protected settings");
        value.PetScale=9;value.UiScale=.1;SettingsStore.Save(value);loaded=SettingsStore.Load();
        Check(loaded.PetScale==3 && loaded.UiScale==.5,"scale values constrained to supported range");
        File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "settings.json"), "{broken");
        Check(SettingsStore.Load().OpenAiKey == "" && SettingsStore.LastLoadWarning != null, "corrupted settings recover with visible warning");
    }

    private static void TestVoiceChunks()
    {
        string text = new string('字', 999) + "😀" + new string('字', 1020);
        var chunks = VoiceService.ChunkText(text);
        Check(chunks.All(x => x.Length <= 1000) && string.Concat(chunks) == text, "voice chunking preserves Unicode without splitting surrogate pairs");
        Check(VoiceService.ChunkText("   ").Count == 0, "empty voice text produces no chunks");
        Check(VoiceService.ChunkText("你好。要玩嗎？好！").SequenceEqual(new[]{"你好。","要玩嗎？","好！"}),"short sentences generate separately at Chinese punctuation");
        Check(VoiceService.ChunkText("Value 3.14. Ready? Yes!").SequenceEqual(new[]{"Value 3.14."," Ready?"," Yes!"}),"English boundaries preserve decimals and ordering");
        Check(VoiceService.ChunkText("她說：「走吧。」接著呢？").SequenceEqual(new[]{"她說：「走吧。」","接著呢？"}),"closing quotation stays with sentence");
    }

    private static async Task TestSentencePlaybackAsync()
    {
        var requested=new List<string>();var played=new List<string>();bool prefetched=false;
        using var handler=new FakeHandler(async (request,ct)=> {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string sentence=body.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString()!;
            requested.Add(sentence);
            var response=new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(sentence))};
            response.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");return response;
        });
        Func<string,CancellationToken,Task> playback=async (file,ct)=> {
            if(played.Count==0)prefetched=requested.Count==2;
            played.Add(await File.ReadAllTextAsync(file,ct));
        };
        var ctor=typeof(VoiceService).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();
        using var voice=(VoiceService)ctor.Invoke(new object[]{new AppSettings { ElevenLabsKey="fake",VoiceId="fake",ElevenLabsModel="eleven_v4" },handler,playback});
        await voice.SpeakAsync("第一句。第二句？第三句。",CancellationToken.None);
        Check(prefetched,"next sentence is requested before current playback completes");
        Check(played.SequenceEqual(new[]{"第一句。","第二句？","第三句。"}),"sentence audio plays exactly once in original order");
        Check(!Directory.EnumerateFiles(Path.Combine(SettingsStore.DataDirectory,"audio")).Any(),"sentence playback cleans all temporary audio files");
    }

    private static async Task TestCloudAsync(string scratch)
    {
        var settings = new AppSettings { OpenAiKey = "fake-openai-key", ElevenLabsKey = "fake-elevenlabs-key", VoiceId = "fake-voice", ElevenLabsModel = "eleven_v4" };
        int call = 0;
        using (var handler = new FakeHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "AI uses Responses endpoint");
            Check(root.GetProperty("model").GetString() == "gpt-6.1-sol" && !root.GetProperty("store").GetBoolean(), "requested model retained and provider storage disabled");
            if (call++ == 0) Check(body.Contains("data:image/png;base64,"), "current screenshot attached to current request");
            else Check(!body.Contains("data:image/png;base64,"), "old screenshots excluded from subsequent requests");
            return JsonResponse("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"你好，我是米亞。\"}]}]}");
        }))
        using (var service = new AiService(settings, handler))
        {
            Check(await service.ReplyAsync("看看這張圖", new byte[] { 1, 2, 3 }, CancellationToken.None) == "你好，我是米亞。", "AI response text parsed");
            await service.ReplyAsync("繼續", null, CancellationToken.None);
        }

        using (var service = new AiService(settings, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("fake-openai-key: echoed-secret") }))))
        {
            bool safe = false;
            try { await service.ReplyAsync("你好", null, CancellationToken.None); }
            catch (InvalidOperationException ex) { safe = !ex.Message.Contains("echoed-secret") && !ex.Message.Contains("fake-openai-key"); }
            Check(safe, "provider errors do not expose echoed credentials");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var service = new AiService(settings, new FakeHandler(async (_, ct) => { await Task.Delay(10000, ct); return JsonResponse("{}"); })))
        {
            cancellation.CancelAfter(30);
            bool cancelled = false;
            try { await service.ReplyAsync("你好", null, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "in-flight AI request obeys cancellation");
        }

        var materialFolder = Path.Combine(scratch, "materials");
        Directory.CreateDirectory(materialFolder);
        foreach (string name in new[] { "kiri_general.mp3", "kiri_happy.mp3", "leafa.mp3", "combination.mp3", "kiri_notes.txt" })
            await File.WriteAllBytesAsync(Path.Combine(materialFolder, name), new byte[] { 1, 2, 3 });
        var selected = VoiceService.FindKiriMaterials(materialFolder);
        Check(selected.Count == 2 && selected.All(p => Path.GetFileName(p).StartsWith("kiri_")), "voice selection excludes leafa, combination and non-audio files");
        using (var voice = new VoiceService(settings, new FakeHandler(async (request, ct) =>
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Check(request.RequestUri!.AbsolutePath == "/v1/voices/add" && form.Contains("kiri_general.mp3") && form.Contains("kiri_happy.mp3") && !form.Contains("leafa.mp3") && !form.Contains("combination.mp3"), "clone request contains only kiri MP3 fixtures");
            return JsonResponse("{\"voice_id\":\"mock-clone\",\"requires_verification\":true}");
        })))
        {
            Check(await voice.CloneKiriAsync(materialFolder, CancellationToken.None) == "mock-clone" && voice.LastCloneRequiresVerification, "clone response preserves verification requirement");
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
