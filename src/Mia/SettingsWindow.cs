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
    private readonly CheckBox _muted=new CheckBox { Content="靜音（保留文字回答）" };
    private readonly CheckBox _startup=new CheckBox { Content="登入 Windows 後自動啟動米亞（安靜啟動）" };
    private readonly ComboBox _mode=new ComboBox();
    private readonly TextBlock _status=Ui.Text("金鑰以 Windows 使用者加密保護，不寫入發布包。",12,Ui.Subtle);
    private CancellationTokenSource? _work;
    private readonly Button _clone;
    private bool _modeDirty, _mutedDirty, _syncing, _closed, _pendingSave;
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
        var controls=new WrapPanel(); panel.Children.Add(controls);
        controls.Children.Add(Ui.Button("重播語音",()=>app.ChatAction(c=>c.Replay())));
        var clearConversation = Ui.Button("清除對話",()=>app.ChatAction(c=>c.ClearConversation()));
        clearConversation.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 38, 48));
        clearConversation.Foreground = System.Windows.Media.Brushes.White;
        clearConversation.FontWeight = FontWeights.SemiBold;
        controls.Children.Add(clearConversation);
        var advanced=new StackPanel { Margin=new Thickness(0,12,0,0) };
        advanced.Children.Add(Ui.Text("OpenAI API Key · gpt-6.1-terra")); StylePassword(_open); _open.Password=app.Settings.OpenAiKey; advanced.Children.Add(_open);
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
        _muted.IsChecked=app.Settings.Muted; _startup.IsChecked=StartupRegistration.IsEnabled;
        foreach(var box in new[]{_muted,_startup}) { box.Foreground=Ui.Ink; box.Margin=new Thickness(0,5,0,12); panel.Children.Add(box); }
        panel.Children.Add(Ui.Text("右鍵人物：開啟／收起介面；右鍵設定頁：返回橫幅。\n右鍵橫幅：收起。顯示介面時暫時陪伴，收起後恢復預設模式。\nCtrl + Shift + M 喚回；退出請用「退出米亞」或系統匣。",12,Ui.Subtle));
        panel.Children.Add(new Expander { Header="聲線與連線設定", Content=advanced, Foreground=Ui.Ink, Margin=new Thickness(0,8,0,12) });
        panel.Children.Add(_status);
        _autoSave.Tick+=(_,_)=>FlushAutoSave();
        _mode.SelectionChanged+=(_,_)=>{if(!_syncing){_modeDirty=true;QueueAutoSave(true);}};
        _muted.Checked+=(_,_)=>{if(!_syncing){_mutedDirty=true;QueueAutoSave(true);}};
        _muted.Unchecked+=(_,_)=>{if(!_syncing){_mutedDirty=true;QueueAutoSave(true);}};
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
            if(!_mutedDirty) { _syncing=true;_muted.IsChecked=app.Settings.Muted;_syncing=false; }
        };
        Closed+=(_,_)=>{_autoSave.Stop();_closed=true;app.Pet.ModeChanged-=RefreshMode;CancelWork();};
        FloatingSurface.Attach(this,app.Pet);
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
        if(_mutedDirty)_app.Settings.Muted=_muted.IsChecked==true;
        if(_modeDirty)_app.Settings.Mode=(_mode.SelectedItem as ComboBoxItem)?.Tag as string??"companion";
        bool startup=_startup.IsChecked==true;
        if(startup!=StartupRegistration.IsEnabled)StartupRegistration.Set(startup, _app.Hermes is not null);
        _app.Settings.StartWithWindows=startup; _app.ApplySettings(); _modeDirty=false;_mutedDirty=false;
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
            Save(); if(_app.Settings.Muted) { _status.Text="請先取消靜音，再試聽。"; return; }
            using var session=new CancellationTokenSource(); _work=session;
            try { _status.Text="正在生成與播放…"; await _app.Voice.SpeakAsync("嗨，我是米亞。今天也一起找出有趣的線索吧！",session.Token); if(!session.IsCancellationRequested&&!_closed&&!_app.IsExiting)_status.Text="試聽結束。"; }
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
            command=$"\"{powershell}\" -NoProfile -WindowStyle Hidden -File \"{launcher}\"";
        }
        using var key=Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(Name,command);
    }
}
