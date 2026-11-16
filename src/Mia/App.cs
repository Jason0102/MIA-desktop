using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Mia.Services;
using Forms = System.Windows.Forms;

namespace Mia;

public sealed partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private RegisteredWaitHandle? _wakeWait;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private Forms.NotifyIcon? _tray;
    private string? _notificationTask;
    private ChatWindow? _chat;
    private SettingsWindow? _settingsWindow;
    private IntPtr _handle;
    private bool _exiting;
    private int _exitCode;
    public bool IsExiting => _exiting;
    public PetWindow Pet { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public AiService Ai { get; private set; } = null!;
    public HermesService? Hermes { get; private set; }
    public string? HermesConfigurationError { get; private set; }
    public VoiceService Voice { get; private set; } = null!;
    private const string InstanceName = @"Local\MIA.Desktop.v3.2";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--loading" && int.TryParse(args[2], out int launcher))
            return new Application().Run(new LoadingWindow(args[1], launcher));
        if(args.Length==2 && args[0]=="--dock-fixture") { InteractionDiagnostics.RunFixture(args[1]);return 0; }
        if(args.Contains("--exit"))
        {
            try { using var signal=EventWaitHandle.OpenExisting(InstanceName + ".Exit"); signal.Set(); } catch(WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        var app = new App { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string instance = e.Args.Contains("--reply-self-test") ? InstanceName + ".ReplyCheck"
            : e.Args.Contains("--questions-self-test") ? InstanceName + ".QuestionsCheck" : InstanceName;
        _mutex = new Mutex(true, instance, out bool first);
        if (!first)
        {
            try { using var wake=EventWaitHandle.OpenExisting(instance + ".Wake"); wake.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Wake");
        _wakeWait = ThreadPool.RegisterWaitForSingleObject(_wake, (_,_) => Dispatcher.BeginInvoke(new Action(Recall)), null, -1, false);
        _exitSignal=new EventWaitHandle(false,EventResetMode.AutoReset,instance + ".Exit");
        _exitWait=ThreadPool.RegisterWaitForSingleObject(_exitSignal,(_,_)=>Dispatcher.BeginInvoke(new Action(ExitApp)),null,-1,false);
        DispatcherUnhandledException += (_,a) => {
            MessageBox.Show("米亞遇到問題，請重新啟動。\n"+a.Exception.GetType().Name, "MIA", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled=true; ExitApp();
        };
        Settings=SettingsStore.Load();
        DiscoverLocalInputs();
        Ai=new AiService(Settings); Voice=new VoiceService(Settings);
        try { Hermes=HermesService.FromEnvironment(); }
        catch (ArgumentException ex) { HermesConfigurationError=ex.Message; }
        Pet=new PetWindow(); MainWindow=Pet;
        Pet.ContentRendered += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("MIA_LAUNCH_PROGRESS") is { Length: > 0 } progress)
                try { File.WriteAllText(progress + ".ready", "ready"); } catch (IOException) { }
        };
        Pet.SetAllowedScreens(Settings.AllowedScreenIds);
        Pet.ChatRequested += CycleWorkSurface;
        Pet.SettingsRequested += ShowSettings;
        Pet.ExitRequested += ExitApp;
        Pet.Show(); Pet.SetMode(Settings.Mode); Pet.InteractionHints=Settings.InteractionHints;
        Pet.Width=192*Settings.PetScale; Pet.Height=208*Settings.PetScale; FloatingSurface.ApplyScale(Settings.UiScale);
        Voice.SpeakingChanged += speaking => Pet.SetSpeaking(speaking);
        Voice.SpeechAnimationChanged += Pet.SetSpeechAnimation;
        Pet.ModeChanged += mode => { Settings.Mode=mode; SettingsStore.Save(Settings); };
        _handle=new WindowInteropHelper(Pet).Handle;
        HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
        bool hotkey=RegisterHotKey(_handle,1,0x0002|0x0004|0x4000,0x4D);
        _tray=new Forms.NotifyIcon { Icon=System.Drawing.SystemIcons.Information, Text="米亞 MIA 3.2 · 桌面夥伴", Visible=true };
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("米亞，回來！",null,(_,_)=>Dispatcher.Invoke(Recall));
        menu.Items.Add("對話",null,(_,_)=>Dispatcher.Invoke(ShowChat));
        menu.Items.Add("工作中心",null,(_,_)=>Dispatcher.Invoke(ShowWorkCenter));
        menu.Items.Add("設定與聲線",null,(_,_)=>Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("顯示／隱藏",null,(_,_)=>Dispatcher.Invoke(()=>{ if(Pet.IsVisible) { _chat?.Hide(); _settingsWindow?.Hide(); Pet.Hide(); } else Recall(); }));
        menu.Items.Add("暫停／繼續活動",null,(_,_)=>Dispatcher.Invoke(()=>Pet.Paused=!Pet.Paused));
        menu.Items.Add("停止語音",null,(_,_)=>Dispatcher.Invoke(Voice.Stop));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出米亞",null,(_,_)=>Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip=menu;
        _tray.DoubleClick += (_,_)=>Dispatcher.Invoke(Recall);
        _tray.BalloonTipClicked += (_,_)=>Dispatcher.Invoke(() =>
        {
            if (_scheduledBalloon) { _scheduledBalloon = false; ShowScheduledNotices(); return; }
            if (_notificationTask is not { } task) { ShowChat(); return; }
            ShowWorkCenter();
            _chat?.SelectResults(true, task);
        });
        _tray.ShowBalloonTip(3000,"米亞已啟動",hotkey?"Ctrl + Shift + M 喚回米亞；右鍵系統匣可完整退出。":"可由系統匣喚回或退出米亞。快捷鍵已被其他程式使用。",Forms.ToolTipIcon.Info);
        if (!e.Args.Any(arg => arg.Contains("--", StringComparison.Ordinal) && arg.Contains("self-test", StringComparison.Ordinal)))
        {
            InitializeScheduledNotices(menu);
            if (Hermes is not null && Environment.GetEnvironmentVariable("MIA_HERMES_KANBAN_ENTRY") is { Length: > 0 })
                _chat ??= new ChatWindow(this);
        }
        if(e.Args.Contains("--settings")) ShowSettings();
        else if(e.Args.Contains("--work-center")) ShowWorkCenter();
        else if(e.Args.Contains("--chat")) ShowChat();
        if(e.Args.Contains("--interaction-self-test"))Dispatcher.BeginInvoke(new Action(()=>_=InteractionDiagnostics.RunAsync(this)));
        if (e.Args.Contains("--questions-self-test")) Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                ShowChat();
                var checks = await _chat!.VerifyQuestionsAsync();
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "questions-check.json"), System.Text.Json.JsonSerializer.Serialize(checks));
                if (checks.Values.Any(passed => !passed)) _exitCode = 1;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "questions-check-error.txt"), ex.ToString());
                _exitCode = 1;
            }
            finally { ExitApp(); }
        }));
        if (e.Args.Contains("--results-self-test"))
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ShowWorkCenter();
                    var checks = _chat!.VerifyResultsPanel();
                    File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "results-check.json"), System.Text.Json.JsonSerializer.Serialize(checks));
                    if (checks.Values.Any(passed => !passed)) _exitCode = 1;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "results-check-error.txt"), ex.ToString());
                    _exitCode = 1;
                }
                finally { ExitApp(); }
            }));
        if(e.Args.Contains("--reply-self-test"))Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                ShowChat();
                var checks = await _chat!.VerifyReplyPresentationAsync();
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "reply-check.json"), System.Text.Json.JsonSerializer.Serialize(checks));
                if (checks.Values.Any(passed => !passed)) _exitCode = 1;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "reply-check-error.txt"), ex.ToString());
                _exitCode = 1;
            }
            finally { ExitApp(); }
        }));
        if(Array.IndexOf(e.Args,"--hermes-self-test-file") is int hermesTest && hermesTest>=0 && hermesTest+1<e.Args.Length)
            Dispatcher.BeginInvoke(new Action(()=>_=RunHermesCheckAsync(e.Args[hermesTest+1])));
        if(e.Args.Contains("--self-test")||e.Args.Contains("--attachment-self-test"))
        {
            var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(2) };
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try
                {
                    ShowChat();
                    if(e.Args.Contains("--attachment-self-test"))
                    {
                        Directory.CreateDirectory(SettingsStore.DataDirectory);
                        File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"attachment-check.json"),System.Text.Json.JsonSerializer.Serialize(new {
                            removedOnlySelectedImage=_chat!.VerifyAttachmentControls(),
                            arbitraryFilesAndRemoval=_chat.VerifyFileAttachmentControls() }));
                    }
                    _chat!.UpdateLayout();
                    var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)_chat.ActualWidth,(int)_chat.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                    image.Render(_chat);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    Directory.CreateDirectory(SettingsStore.DataDirectory);
                    using(var file=File.Create(Path.Combine(SettingsStore.DataDirectory,"chat-preview.png")))encoder.Save(file);
                    NativeDesktop.GetWindowRect(new WindowInteropHelper(_chat).Handle,out var panelBounds);
                    NativeDesktop.GetWindowRect(new WindowInteropHelper(Pet).Handle,out var petBounds);
                    var area=NativeDesktop.WorkArea(new NativeDesktop.Point { X=petBounds.Left+petBounds.Width/2,Y=petBounds.Top+petBounds.Height/2 });
                    File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"lifecycle-check.json"),System.Text.Json.JsonSerializer.Serialize(new { started=true, petVisible=Pet.IsVisible, chatLoaded=_chat.IsLoaded,
                        borderless=_chat.WindowStyle==WindowStyle.None, transparent=_chat.AllowsTransparency, taskbarHidden=!_chat.ShowInTaskbar,
                        followsPet=_chat.Owner==Pet, abovePet=panelBounds.Bottom<=petBounds.Top,
                        withinWorkArea=panelBounds.Left>=area.Left && panelBounds.Right<=area.Right && panelBounds.Top>=area.Top && panelBounds.Bottom<=area.Bottom,
                        holdsPetStill=Pet.InteractionOpen, openAiImported=!string.IsNullOrWhiteSpace(Settings.OpenAiKey), elevenImported=!string.IsNullOrWhiteSpace(Settings.ElevenLabsKey), process=Environment.ProcessId }));
                }
                finally { ExitApp(); }
            }; timer.Start();
        }
    }
    public void Recall() { if(_exiting)return; Pet.Show(); Pet.ReturnToCursor(); }
    private void DiscoverLocalInputs()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        for(int depth=0; dir!=null && depth<7; depth++,dir=dir.Parent)
        {
            string key=Path.Combine(dir.FullName,"api_key");
            if((string.IsNullOrWhiteSpace(Settings.OpenAiKey)||string.IsNullOrWhiteSpace(Settings.ElevenLabsKey)) && File.Exists(key))
            { try { SettingsStore.TryImportKeys(key,Settings); } catch { /* User can import manually in settings. */ } }
            string materials=Path.Combine(dir.FullName,"voice","materials");
            if(string.IsNullOrEmpty(Settings.VoiceMaterialsPath)&&Directory.Exists(materials))Settings.VoiceMaterialsPath=materials;
            string profile=Path.Combine(dir.FullName,"voice-profile.json");
            if(string.IsNullOrWhiteSpace(Settings.VoiceId)&&File.Exists(profile))
            {
                try { using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(profile));
                    if(json.RootElement.TryGetProperty("voice_id",out var id))Settings.VoiceId=id.GetString()??"";
                } catch(System.Text.Json.JsonException) { }
            }
        }
    }
    private void CycleWorkSurface()
    {
        if(_exiting)return;
        if(_chat?.IsVisible!=true) ShowChat();
        else if(!_chat.IsWorkCenter) ShowWorkCenter();
        else
        {
            _chat.Hide();
        }
    }
    public void ShowChat() => ShowWorkSurface(false);
    public void ShowWorkCenter() => ShowWorkSurface(true);
    private void ShowWorkSurface(bool workCenter)
    {
        if(_exiting)return;
        Pet.Show();
        _settingsWindow?.Hide();
        _chat ??= new ChatWindow(this);
        _chat.SetWorkCenter(workCenter);
        _chat.Show(); _chat.WindowState=WindowState.Normal; _chat.Activate(); Pet.InteractionOpen=true;
    }
    public void ShowSettings()
    {
        if(_exiting)return;
        Pet.Show();
        _chat?.Hide();
        if(_settingsWindow==null) { _settingsWindow=new SettingsWindow(this); _settingsWindow.Closed+=(_,_)=>_settingsWindow=null; }
        _settingsWindow.Show(); _settingsWindow.Activate(); Pet.InteractionOpen=true;
    }
    internal void NotifyTaskResult(string taskTitle, bool hasResult, string? taskId = null)
    {
        if (_exiting || _tray is null) return;
        _scheduledBalloon = false;
        _notificationTask = taskId;
        _tray.ShowBalloonTip(5000, hasResult ? "米亞 · 任務結果已回傳" : "米亞 · 任務未完成",
            $"「{taskTitle}」\n點此查看結果。", Forms.ToolTipIcon.Info);
    }
    public Task ClearHermesMemoryAsync()
    {
        _chat ??= new ChatWindow(this);
        return _chat.ClearHermesMemoryAsync();
    }
    public void OpenExtensionTask()
    {
        ShowChat();
        _chat?.DraftTask("請使用 mia_install_extension 安裝並驗證米亞內附的診斷擴充。");
    }
    public void ApplySettings()
    {
        SettingsStore.Save(Settings); Pet.SetMode(Settings.Mode); Pet.InteractionHints=Settings.InteractionHints;
        Pet.Width=192*Settings.PetScale; Pet.Height=208*Settings.PetScale; FloatingSurface.ApplyScale(Settings.UiScale);
        if(Settings.Muted)Voice.Stop();
        Pet.SetAllowedScreens(Settings.AllowedScreenIds);
    }
    private async Task RunHermesCheckAsync(string promptPath)
    {
        int exitCode=1;
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            if(Hermes is null) throw new InvalidOperationException(HermesConfigurationError ?? "未配置 Hermes 工作模式。");
            if(new FileInfo(promptPath).Length>32_000) throw new ArgumentException("測試訊息太長。");
            // The same chat control and server path as a manually submitted task.
            Settings.Muted=true;
            ShowChat();
            if (Environment.GetEnvironmentVariable("MIA_PARALLEL_CHECK") == "1")
            {
                var parallelResult = await _chat!.VerifyParallelJobsAsync(File.ReadAllText(promptPath));
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"parallel-ui-check.json"),
                    System.Text.Json.JsonSerializer.Serialize(parallelResult));
                if(parallelResult.Success) exitCode=0;
            }
            else
            {
                var result=await _chat!.VerifyHermesAsync(File.ReadAllText(promptPath));
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"hermes-ui-check.json"),System.Text.Json.JsonSerializer.Serialize(result));
                if(result.Success)exitCode=0;
            }
        }
        catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"hermes-ui-check.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Success=false, Error=ChatWindow.Friendly(ex) }));
        }
        _exitCode=exitCode;
        ExitApp();
    }
    public async void ExitApp()
    {
        if(_exiting)return; _settingsWindow?.FlushAutoSave();_exiting=true;
        CaptureService.CancelActive();
        _chat?.CancelReply(); _settingsWindow?.CancelWork(); Voice?.Stop(); Pet?.Stop();
        if(_chat is not null && Hermes is not null) await _chat.WaitForRequestAsync();
        try { if(Settings!=null) SettingsStore.Save(Settings); } catch { }
        Shutdown(_exitCode);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        StopScheduledNotices();
        _wakeWait?.Unregister(null); _wake?.Dispose();
        _exitWait?.Unregister(null); _exitSignal?.Dispose();
        if(_handle!=IntPtr.Zero)UnregisterHotKey(_handle,1);
        if(_tray!=null) { _tray.Visible=false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        Voice?.Dispose(); Ai?.Dispose(); Hermes?.Dispose(); Pet?.Stop();
        _mutex?.Dispose(); base.OnExit(e);
    }
    private IntPtr WindowHook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled)
    {
        if(msg==0x0312 && w.ToInt32()==1) { Recall(); handled=true; }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}
