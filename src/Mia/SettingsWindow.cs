using System.Windows.Input;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mia.Services;

namespace Mia;

public sealed partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly PasswordBox _open=new PasswordBox();
    private readonly PasswordBox _eleven=new PasswordBox();
    private readonly TextBox _voice;
    private readonly TextBox _materials;
    private readonly CheckBox _voiceGeneration=new CheckBox { Content="語音生成（ElevenLabs）" };
    private readonly CheckBox _startup=new CheckBox { Content="登入 Windows 後自動啟動米亞（安靜啟動）" };
    private readonly ComboBox _mode=new ComboBox();
    private readonly TextBlock _status=Ui.Text("金鑰以 Windows 使用者加密保護，不寫入發布包。",12,Ui.Subtle);
    private CancellationTokenSource? _work;
    private readonly Button _clone;
    private bool _modeDirty, _voiceGenerationDirty, _syncing, _closed, _pendingSave;
    private readonly System.Windows.Threading.DispatcherTimer _autoSave=new() {Interval=TimeSpan.FromMilliseconds(600)};

    public SettingsWindow(App app)
    {
        _app=app; Title="米亞 MIA 3.2 · 設定與聲線"; Width=400; Height=480; MinWidth=320; MinHeight=180; Ui.Style(this);
        var panel=new StackPanel { Margin=new Thickness(16) }; Content=new ScrollViewer { Content=panel, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text("米亞 · 設定",16,Ui.Gold));
        var navigation=new WrapPanel(); panel.Children.Add(navigation);
        navigation.Children.Add(Ui.Button("退出米亞",()=>{FlushAutoSave();app.ExitApp();}));
        navigation.Children.Add(Ui.Button("暫停／繼續",()=>app.Pet.Paused=!app.Pet.Paused));
        panel.Children.Add(Ui.Text("變更自動套用 · 右鍵返回對話",13,Ui.Subtle));
        var advanced=new StackPanel { Margin=new Thickness(0,12,0,0) };
        advanced.Children.Add(Ui.Text("OpenAI API Key · gpt-6.1-sol")); StylePassword(_open); _open.Password=app.Settings.OpenAiKey; advanced.Children.Add(_open);
        advanced.Children.Add(Ui.Text("ElevenLabs API Key · Eleven v4")); StylePassword(_eleven); _eleven.Password=app.Settings.ElevenLabsKey; advanced.Children.Add(_eleven);
        advanced.Children.Add(Ui.Button("從金鑰檔案匯入",ImportKeys));
        advanced.Children.Add(Ui.Text("Voice ID（已有聲線可直接填入）")); _voice=Ui.Input(app.Settings.VoiceId); advanced.Children.Add(_voice);
        advanced.Children.Add(Ui.Text("語音素材資料夾 · 僅採用 kiri_*.mp3")); _materials=Ui.Input(app.Settings.VoiceMaterialsPath); advanced.Children.Add(_materials);
        advanced.Children.Add(Ui.Button("選擇素材資料夾",()=> { using var dialog=new System.Windows.Forms.FolderBrowserDialog(); if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)_materials.Text=dialog.SelectedPath; }));
        var voiceActions=new WrapPanel(); advanced.Children.Add(voiceActions);
        _clone=Ui.Button("建立 kiri 聲線",()=>_=CloneAsync(),true); voiceActions.Children.Add(_clone);
        voiceActions.Children.Add(Ui.Button("試聽米亞",()=>_=PreviewAsync()));
        voiceActions.Children.Add(Ui.Button("停止",CancelWork));
        advanced.Children.Add(Ui.Text("建立聲線會將下方資料夾中的 kiri 音訊傳給 ElevenLabs；可先檢查檔案清單，再建立。",12,Ui.Subtle));
        var hints=new CheckBox { Content="輔助功能 · 顯示互動提示", Foreground=Ui.Ink, IsChecked=app.Settings.InteractionHints, Margin=new Thickness(0,8,0,12) };
        hints.Checked+=(_,_)=>{app.Settings.InteractionHints=true;app.ApplySettings();};
        hints.Unchecked+=(_,_)=>{app.Settings.InteractionHints=false;app.ApplySettings();};
        panel.Children.Add(hints);
        void AddScale(string title,double initial,Action<double> apply)
        {
            var label=Ui.Text($"{title} · {initial:0.0} 倍");panel.Children.Add(label);
            var slider=new Slider { Minimum=.5,Maximum=3,Value=initial,TickFrequency=.1,
                IsSnapToTickEnabled=true,SmallChange=.1,LargeChange=.5,Margin=new Thickness(0,4,0,14) };
            double applied=initial;
            void Commit(){if(Math.Abs(applied-slider.Value)<.001)return;applied=slider.Value;apply(applied);app.ApplySettings();}
            slider.ValueChanged+=(_,e)=>{label.Text=$"{title} · {e.NewValue:0.0} 倍";};
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,new System.Windows.Controls.Primitives.DragCompletedEventHandler((_,_)=>Commit()));
            slider.AddHandler(Mouse.PreviewMouseUpEvent,new MouseButtonEventHandler((_,e)=>{if(e.ChangedButton==MouseButton.Left)Dispatcher.BeginInvoke(new Action(Commit));}),true);
            slider.KeyUp+=(_,_)=>Commit();
            panel.Children.Add(slider);
        }
        AddScale("人物大小",app.Settings.PetScale,value=>app.Settings.PetScale=value);
        AddScale("介面大小（字體與物件）",app.Settings.UiScale,value=>app.Settings.UiScale=value);
        panel.Children.Add(_screenChoices);RefreshScreenChoices();
        panel.Children.Add(Ui.Text("玩球"));
        panel.Children.Add(Ui.Button("放出／收起乒乓球",()=>{app.Pet.ToggleBall();Hide();}));
        var behavior=Ui.Text("",12,Ui.Subtle);panel.Children.Add(behavior);
        var statusTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
        statusTimer.Tick+=(_,_)=>{behavior.Text=app.Pet.BehaviorStatus;RefreshScreenChoices();};
        IsVisibleChanged+=(_,_)=>{if(IsVisible){RefreshScreenChoices();statusTimer.Start();}else statusTimer.Stop();};
        Closed+=(_,_)=>statusTimer.Stop();
        panel.Children.Add(Ui.Text("預設互動模式（收起介面後恢復）"));
        _mode.Items.Add(new ComboBoxItem { Content="陪伴 · 留在原地", Tag="companion" });
        _mode.Items.Add(new ComboBoxItem { Content="探索 · 自主走動", Tag="explore" });
        _mode.SelectedIndex=app.Settings.Mode switch { "explore"=>1,_=>0 }; _mode.Margin=new Thickness(0,0,0,16); panel.Children.Add(_mode);
        _voiceGeneration.IsChecked=!app.Settings.Muted; _startup.IsChecked=StartupRegistration.IsEnabled;
        foreach(var box in new[]{_voiceGeneration,_startup}) { box.Foreground=Ui.Ink; box.Margin=new Thickness(0,5,0,12); panel.Children.Add(box); }
        panel.Children.Add(Ui.Text("右鍵人物依序切換輸入橫幅、工作中心與收起。右鍵設定頁可回到輸入橫幅。\nCtrl + Shift + M 喚回；退出請用「退出米亞」或系統匣。",12,Ui.Subtle));
        panel.Children.Add(new Expander { Header="聲線與連線設定", Content=advanced, Foreground=Ui.Ink, Margin=new Thickness(0,8,0,12) });
        panel.Children.Add(_status);
        AddAgentSettings(panel);
        _autoSave.Tick+=(_,_)=>FlushAutoSave();
        _mode.SelectionChanged+=(_,_)=>{if(!_syncing){_modeDirty=true;QueueAutoSave(true);}};
        _voiceGeneration.Checked+=(_,_)=>VoiceGenerationChanged();
        _voiceGeneration.Unchecked+=(_,_)=>VoiceGenerationChanged();
        void VoiceGenerationChanged()
        {
            if (_syncing) return;
            _voiceGenerationDirty = true;
            QueueAutoSave(true);
        }
        _startup.Checked+=(_,_)=>QueueAutoSave(true);_startup.Unchecked+=(_,_)=>QueueAutoSave(true);
        _open.PasswordChanged+=(_,_)=>QueueAutoSave();_eleven.PasswordChanged+=(_,_)=>QueueAutoSave();
        _voice.TextChanged+=(_,_)=>QueueAutoSave();_materials.TextChanged+=(_,_)=>QueueAutoSave();
        foreach(var input in new Control[]{_open,_eleven,_voice,_materials})input.LostKeyboardFocus+=(_,_)=>FlushAutoSave();
        PreviewMouseRightButtonUp+=(_,e)=>{e.Handled=true;FlushAutoSave();Hide();app.ShowChat();};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)FlushAutoSave();};
        Closing+=(_,_)=>FlushAutoSave();
        void RefreshMode(string mode)
        {
            if(_modeDirty)return;
            _syncing=true;
            _mode.SelectedIndex=mode switch { "explore"=>1,_=>0 };
            _syncing=false;
        }
        app.Pet.ModeChanged+=RefreshMode;
        Activated+=(_,_)=>
        {
            RefreshMode(app.Settings.Mode);
            if(!_voiceGenerationDirty) { _syncing=true;_voiceGeneration.IsChecked=!app.Settings.Muted;_syncing=false; }
        };
        Closed+=(_,_)=>{_autoSave.Stop();_closed=true;app.Pet.ModeChanged-=RefreshMode;CancelWork();};
        FloatingSurface.Attach(this,app.Pet);
    }

    private void AddAgentSettings(StackPanel panel)
    {
        string? workspace = Environment.GetEnvironmentVariable("MIA_CODEX_WORKSPACE");
        string? codex = Environment.GetEnvironmentVariable("MIA_CODEX_EXECUTABLE");
        string? profile = Environment.GetEnvironmentVariable("HERMES_HOME");
        var section = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(new Border { Child = section, Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(10), Background = Ui.Panel });
        section.Children.Add(Ui.Text("代理與權限", 16, Ui.Gold));

        section.Children.Add(Ui.Text("Hermes 主代理"));
        var connection = Ui.Text(_app.Hermes is null ? _app.HermesConfigurationError ?? "未啟用。請從米亞 3.2 啟動捷徑開啟。" : "已設定本機 API。", 12, Ui.Subtle);
        section.Children.Add(connection);
        section.Children.Add(Ui.Text("Codex 執行助手"));
        section.Children.Add(Ui.Text(File.Exists(codex) ? $"已找到執行程式\n{codex}" : "未找到執行程式。", 12, Ui.Subtle));
        section.Children.Add(Ui.Text("Hermes 擴充"));
        var extensions = Ui.Text("", 12, Ui.Subtle);
        section.Children.Add(extensions);
        var refresh = Ui.Button("檢查 Hermes 連線", () => { });
        refresh.Click += (_, _) => _ = RefreshAsync();
        refresh.IsEnabled = _app.Hermes is not null;
        section.Children.Insert(3, refresh);
        var install = Ui.Button("開啟擴充安裝任務", _app.OpenExtensionTask);
        install.IsEnabled = _app.Hermes is not null;
        section.Children.Add(install);
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshExtensions(); };
        RefreshExtensions();

        section.Children.Add(Ui.Text("應用程式連線"));
        section.Children.Add(Ui.Text("Hermes 可透過 computer use 操作桌面，並以 PowerPoint 檢查工具匯出簡報。需要確認的操作會顯示於工作中心。", 12, Ui.Subtle));
        section.Children.Add(Ui.Text("工作目錄與權限"));
        section.Children.Add(Ui.Text("任務檔案可讀寫 C 槽；新增程式、測試與暫存檔保留於工作目錄。Windows 的受保護目錄仍遵循系統權限。", 12, Ui.Subtle));
        section.Children.Add(Ui.Text(workspace ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIA Workspace"), 12, Ui.Subtle));
        if (Directory.Exists(workspace))
            section.Children.Add(Ui.Button("開啟工作目錄", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(workspace!) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { connection.Text = "無法開啟工作目錄。"; }
            }));

        var feedback = Ui.Text("", 12, Ui.Subtle);
        var clear = Ui.Button("清除 Hermes 的記憶", () => { });
        clear.Click += (_, _) => _ = ClearAsync();
        clear.IsEnabled = _app.Hermes is not null;
        clear.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 38, 48));
        clear.Foreground = System.Windows.Media.Brushes.White;
        section.Children.Add(clear);
        section.Children.Add(feedback);

        void RefreshExtensions()
        {
            bool installed = profile is not null && File.Exists(Path.Combine(profile, "plugins", "mia-install-check", "plugin.yaml"));
            extensions.Text = installed ? "已找到內附診斷擴充。" : "內附診斷擴充尚未安裝。";
        }

        async Task RefreshAsync()
        {
            refresh.IsEnabled = false;
            try { connection.Text = await _app.Hermes!.ConnectionStatusAsync(); RefreshExtensions(); }
            catch (Exception ex) { connection.Text = ChatWindow.Friendly(ex); }
            finally { refresh.IsEnabled = true; }
        }

        async Task ClearAsync()
        {
            if (MessageBox.Show(this, "將永久清除米亞的 Hermes 對話紀錄、內建長期記憶及個人偏好記憶。已產出的工作檔案會保留。確定要清除嗎？",
                "清除 Hermes 的記憶", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            clear.IsEnabled = false;
            feedback.Text = "正在清除 Hermes 的記憶…";
            try { await _app.ClearHermesMemoryAsync(); feedback.Text = "已清除 Hermes 的記憶與對話紀錄。"; }
            catch (Exception ex) { feedback.Text = "清除未完成。 " + ChatWindow.Friendly(ex); }
            finally { clear.IsEnabled = true; }
        }
    }
    private void QueueAutoSave(bool immediate=false)
    {
        if(_syncing||_closed||_app.IsExiting)return;_pendingSave=true;_autoSave.Stop();
        if(immediate)FlushAutoSave();else _autoSave.Start();
    }
    internal void FlushAutoSave()
    {
        _autoSave.Stop();if(!_pendingSave||_closed||_app.IsExiting)return;
        try {Save();_pendingSave=false;_status.Text="已自動套用。";}
        catch(Exception ex){_status.Text=ChatWindow.Friendly(ex);}
    }
    private void StylePassword(PasswordBox box) { box.Background=Ui.Panel; box.Foreground=Ui.Ink; box.Padding=new Thickness(9); box.Margin=new Thickness(0,0,0,12); }
    private void Save()
    {
        _app.Settings.OpenAiKey=_open.Password.Trim(); _app.Settings.ElevenLabsKey=_eleven.Password.Trim();
        _app.Settings.VoiceId=_voice.Text.Trim(); _app.Settings.VoiceMaterialsPath=_materials.Text.Trim();
        if(_voiceGenerationDirty)_app.Settings.Muted=_voiceGeneration.IsChecked!=true;
        if(_modeDirty)_app.Settings.Mode=(_mode.SelectedItem as ComboBoxItem)?.Tag as string??"companion";
        bool startup=_startup.IsChecked==true;
        if(startup!=StartupRegistration.IsEnabled)StartupRegistration.Set(startup, _app.Hermes is not null);
        _app.Settings.StartWithWindows=startup; _app.ApplySettings(); _modeDirty=false;_voiceGenerationDirty=false;
    }
    private void ImportKeys()
    {
        var picker=new OpenFileDialog { Title="選擇金鑰檔案", Filter="所有檔案|*.*" };
        if(picker.ShowDialog(this)!=true)return;
        try
        {
            if(!SettingsStore.TryImportKeys(picker.FileName,_app.Settings))throw new InvalidOperationException("未找到可辨識的金鑰；請使用 OpenAI 與 ElevenLabs 標籤。");
            _syncing=true;_open.Password=_app.Settings.OpenAiKey; _eleven.Password=_app.Settings.ElevenLabsKey;_syncing=false; _status.Text="已匯入加密儲存；原始檔未刪除。";
        }
        catch(Exception ex) { if(!_closed&&!_app.IsExiting)_status.Text=ChatWindow.Friendly(ex); }
    }
    private async Task CloneAsync()
    {
        if(_work!=null||_closed||_app.IsExiting)return;
        try
        {
            Save(); var files=VoiceService.FindKiriMaterials(_materials.Text.Trim());
            string list=string.Join("\n",files.Select(Path.GetFileName));
            if(MessageBox.Show(this,"將使用以下素材建立新聲線：\n\n"+list+"\n\n我確認有權複製此聲音，並同意上傳至 ElevenLabs。是否建立？", "建立 kiri 聲線",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            using var session=new CancellationTokenSource(); _work=session; _clone.IsEnabled=false;
            try
            {
                _status.Text="正在建立 kiri 聲線…";
                string id=await _app.Voice.CloneKiriAsync(_materials.Text.Trim(),session.Token);
                session.Token.ThrowIfCancellationRequested();
                if(_closed||_app.IsExiting)return;
                _voice.Text=id; _app.Settings.VoiceId=id; SettingsStore.Save(_app.Settings);
                _status.Text=_app.Voice.LastCloneRequiresVerification?"聲線已建立，需先到 ElevenLabs 完成驗證才能使用。":"聲線已建立並儲存，可以試聽了。";
            }
            finally { if(ReferenceEquals(_work,session))_work=null; if(!_closed)_clone.IsEnabled=true; }
        }
        catch(OperationCanceledException) { if(!_closed&&!_app.IsExiting)_status.Text="已停止等待；若已上傳，請先到 ElevenLabs 檢查是否已建立聲線，避免重複建立。"; }
        catch(Exception ex) { if(!_closed&&!_app.IsExiting)_status.Text=ChatWindow.Friendly(ex); }
    }
    private async Task PreviewAsync()
    {
        if(_work!=null||_closed||_app.IsExiting)return;
        try
        {
            Save(); if(_app.Settings.Muted) { _status.Text="請先開啟語音生成，再試聽。"; return; }
            using var session=new CancellationTokenSource(); _work=session;
            try
            {
                _status.Text = "正在生成與播放…";
                await _app.Voice.SpeakAsync("嗨，我是米亞。今天也一起找出有趣的線索吧！", session.Token);
                if (!session.IsCancellationRequested && !_closed && !_app.IsExiting)
                    _status.Text = "ElevenLabs 試聽結束。";
            }
            finally { if(ReferenceEquals(_work,session))_work=null; }
        }
        catch(OperationCanceledException) { if(!_closed&&!_app.IsExiting)_status.Text="已停止。"; }
        catch(Exception ex) { if(!_closed&&!_app.IsExiting)_status.Text=ChatWindow.Friendly(ex); }
    }
    public void CancelWork() { if(_work is { } work) { work.Cancel(); _app.Voice.Stop(); } }
}

internal static class StartupRegistration
{
    private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name="MIA.Desktop.3.2";
    public static bool IsEnabled { get { using var key=Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) is string; } }
    public static void Set(bool enabled, bool useHermes)
    {
        if(!enabled)
        {
            using var existing=Registry.CurrentUser.OpenSubKey(Key,writable:true);
            existing?.DeleteValue(Name,false);
            return;
        }

        string command=$"\"{Environment.ProcessPath}\" --quiet";
        if(useHermes)
        {
            string? launcher=Environment.GetEnvironmentVariable("MIA_START_LAUNCHER");
            if(string.IsNullOrWhiteSpace(launcher) || !Path.IsPathFullyQualified(launcher) || !File.Exists(launcher))
                throw new InvalidOperationException("請使用 3.2 的啟動捷徑，再啟用登入時自動啟動。");
            string powershell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
            command=$"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{launcher}\"";
        }
        using var key=Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(Name,command);
    }
}
