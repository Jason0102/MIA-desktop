using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow : Window
{
    private readonly App _app;
    private readonly StackPanel _messages=new StackPanel();
    private readonly ScrollViewer _scroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    private readonly TextBox _input=Ui.Input();
    private readonly TextBlock _status=Ui.Text("",12,Ui.Subtle);
    private readonly WrapPanel _previews=new WrapPanel();
    private readonly TextBlock _attachmentLabel=Ui.Text("",12,Ui.Subtle);
    private readonly Button _send;
    private readonly Button _stop;
    private readonly Border _details=new Border { Visibility=Visibility.Collapsed };
    private CancellationTokenSource? _request;
    private Task _hermesRequest = Task.CompletedTask;
    private bool _clearingConversation;
    private readonly StackPanel _controls = new();
    private readonly Dictionary<HermesControl, FrameworkElement> _pendingControls = new();
    private readonly List<string> _hermesToolEvents = new();
    private string? _hermesFinalReply;
    private sealed record Attachment(byte[] Bytes, DateTime Time);
    private readonly List<Attachment> _attachments=new();
    private readonly List<string> _fileAttachments = new();
    private bool _capturing;
    private bool _composing, _commitPending;
    private CancellationTokenSource? _captureCancellation;
    private readonly ScrollViewer _detailScroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto, MaxHeight=340 };
    private readonly DockPanel _workPanel = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _activity = new();
    private readonly StackPanel _bannerDetails = new();
    private Border _capsule = null!;
    internal bool IsWorkCenter { get; private set; }
    private string _lastReply="";
    public bool HasActiveReply => _request!=null || _jobs.Count > 0 || _dispatches.Any(task => !task.IsCompleted);

    public ChatWindow(App app)
    {
        _app=app; Title="米亞 MIA 3.2 · 對話"; Width=380; MinWidth=280; MinHeight=56;
        SizeToContent=SizeToContent.Height; Ui.Style(this);
        var root=new StackPanel(); Content=root;
        var details=new StackPanel { Margin=new Thickness(14) };
        InitializeResults();
        details.Children.Add(_workPanel);
        _workPanel.Visibility = Visibility.Collapsed;
        _scroll.Content=_messages; _scroll.MaxHeight=170;
        _bannerDetails.Children.Add(_scroll); _bannerDetails.Children.Add(_status);
        _bannerDetails.Children.Add(_controls);
        _bannerDetails.Children.Add(_previews); _bannerDetails.Children.Add(_attachmentLabel);
        details.Children.Add(_bannerDetails);
        _detailScroll.Content=details; _details.Child=_detailScroll; _details.CornerRadius=new CornerRadius(16);
        _details.Background=new SolidColorBrush(Color.FromArgb(205,56,56,56));
        _details.Margin=new Thickness(0,0,0,8); root.Children.Add(_details);
        var capsule=new Border { Height=56, CornerRadius=new CornerRadius(28),
            Background=new SolidColorBrush(Color.FromArgb(170,100,100,100)), Padding=new Thickness(20,8,10,8) };
        _capsule = capsule;
        var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        capsule.Child=row; root.Children.Add(capsule);
        _stop=IconButton("\uE711","停止工作或語音",CancelReply);
        _stop.Visibility=Visibility.Collapsed;_stop.HorizontalAlignment=HorizontalAlignment.Center;
        _stop.Background=new SolidColorBrush(Color.FromArgb(190,65,65,65));root.Children.Add(_stop);
        var stopTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        stopTimer.Tick+=(_,_)=>_stop.Visibility=(!IsWorkCenter && (HasActiveReply||app.Voice.IsSpeaking))?Visibility.Visible:Visibility.Collapsed;
        _stop.ToolTip = "停止所有工作與語音";
        IsVisibleChanged+=(_,_)=>{if(IsVisible)stopTimer.Start();else stopTimer.Stop();};
        Closed+=(_,_)=>stopTimer.Stop();
        var inputLayer=new Grid(); row.Children.Add(inputLayer);
        var placeholder=new TextBlock { Text="跟米亞說點什麼…", Foreground=new SolidColorBrush(Color.FromRgb(220,220,220)),
            VerticalAlignment=VerticalAlignment.Center, IsHitTestVisible=false, FontSize=14 };
        inputLayer.Children.Add(placeholder);
        var networkTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
        void RefreshNetwork()
        {
            if (app.Hermes is not null) { placeholder.Text="交給米亞一項工作…"; return; }
            if (app.HermesConfigurationError is not null) { placeholder.Text="請重新啟動 Hermes 工作模式"; return; }
            bool online=false;
            try { var type=Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                dynamic manager=Activator.CreateInstance(type!)!;
                try { online=manager.IsConnectedToInternet; }
                finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(manager); }
            } catch { online=System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
            placeholder.Text=online?"跟米亞說點什麼…":"未連線無法對話";
        }
        networkTimer.Tick+=(_,_)=>RefreshNetwork();
        IsVisibleChanged+=(_,_)=>{if(IsVisible){RefreshNetwork();networkTimer.Start();}else networkTimer.Stop();};
        Closed+=(_,_)=>networkTimer.Stop();

        _input.Background=Brushes.Transparent; _input.BorderThickness=new Thickness(0);
        _input.Padding=new Thickness(0,7,8,0); _input.Margin=new Thickness(0);
        _input.Height=38; _input.AcceptsReturn=false; _input.MaxLength=8000;
        _input.VerticalContentAlignment=VerticalAlignment.Center;
        _input.ToolTip="輸入文字後按 Enter 傳送";
        _input.TextChanged+=(_,_)=>placeholder.Visibility=string.IsNullOrEmpty(_input.Text)?Visibility.Visible:Visibility.Collapsed;
        inputLayer.Children.Add(_input);
        TextCompositionManager.AddPreviewTextInputStartHandler(_input,(_,_)=>_composing=true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_input,(_,_)=>_composing=true);
        TextCompositionManager.AddPreviewTextInputHandler(_input,(_,_)=>
        {
            if(!_composing)return;
            _composing=false; _commitPending=true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>_commitPending=false));
        });
        _input.GotKeyboardFocus+=(_,_)=>app.Pet.BeginListening(true);
        _input.LostKeyboardFocus+=(_,_)=>{_composing=false;_commitPending=false;app.Pet.BeginListening(false);};
        _input.PreviewKeyDown+=(_,e)=>
        {
            if(ShouldSubmitEnter(e.Key,_composing,_commitPending)) {e.Handled=true;_=SendAsync();}
        };
        _send=IconButton("\uE122","傳送",()=>_=SendAsync()); Grid.SetColumn(_send,1);row.Children.Add(_send);
        var capture=IconButton("\uE722","框選畫面",()=>_=CaptureAsync(false)); Grid.SetColumn(capture,2);row.Children.Add(capture);
        var settings=IconButton("\uE713","設定",app.ShowSettings); Grid.SetColumn(settings,3);row.Children.Add(settings);
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock))!
            .AddValueChanged(_status,(_,_)=>{ if(!string.IsNullOrWhiteSpace(_status.Text))_details.Visibility=Visibility.Visible; });
        if (app.Hermes is { } hermes)
        {
            foreach (var turn in hermes.History)
            {
                AddMessage("你", turn.User, false);
                var result = _taskResults.Entries.FirstOrDefault(item => item.Reply == turn.Assistant);
                string visible = result is null ? turn.Assistant : TaskCompletion.Display(turn.Assistant, result.Files.Length > 0);
                var reply = AddMessage("米亞", visible, true);
                if (result is not null && reply.Parent is StackPanel panel) panel.Children.Add(BuildResultCard(result, full: false));
                _lastReply = visible;
            }
            _status.Text = hermes.MemoryWarning ?? "";
        }
        else foreach (var item in app.Ai.History)
        {
            AddMessage(item.Role == "user" ? "你" : "米亞", item.Text, item.Role == "assistant");
            if (item.Role == "assistant") _lastReply = item.Text.Split("\n來源：")[0];
        }
        if (app.HermesConfigurationError is not null) _status.Text = app.HermesConfigurationError;
        else if (app.Hermes is null && (app.Ai.MemoryWarning != null || app.Ai.RoleWarning != null)) _status.Text = app.Ai.MemoryWarning ?? app.Ai.RoleWarning!;
        Closing+=(_,e)=> { if(!_app.IsExiting) { e.Cancel=true; Hide(); } };
        PreviewMouseRightButtonUp+=(_,e)=>{e.Handled=true;Hide();};
        AllowDrop=true;
        PreviewDragOver+=(_,e)=>{ e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)||e.Data.GetDataPresent(DataFormats.UnicodeText)?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true; };
        PreviewDrop+=OnDrop;
        FloatingSurface.Attach(this,app.Pet,wrap:false,suppressEscape:()=>_composing||_commitPending,
            preferredSize:()=> (380, double.NaN));
        IsVisibleChanged+=(_,_)=> { if(IsVisible)Dispatcher.BeginInvoke(new Action(()=> { if(IsVisible && !IsWorkCenter && !_app.IsExiting)_input.Focus(); })); else app.Pet.BeginListening(false); };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(MaxHeightProperty,typeof(Window))!
            .AddValueChanged(this,(_,_)=>UpdateWorkLayout());
    }
    internal void SetWorkCenter(bool enabled)
    {
        IsWorkCenter = enabled;
        Title = enabled ? "米亞 MIA 3.2 · 工作中心" : "米亞 MIA 3.2 · 輸入橫幅";
        _workPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _bannerDetails.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _capsule.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = Visibility.Collapsed;
        _details.Visibility = enabled || _messages.Children.Count > 0 || _attachments.Count > 0 || _fileAttachments.Count > 0 || _status.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        FloatingSurface.RefreshScale(this, _app.Settings.UiScale);
        UpdateWorkLayout();
        if (enabled) SelectResults(false);
        _scroll.ScrollToEnd();
    }

    private void UpdateWorkLayout()
    {
        _scroll.MaxHeight = IsWorkCenter ? 350 : 170;
        double available = MaxHeight / _app.Settings.UiScale - 112;
        _detailScroll.MaxHeight = IsWorkCenter ? double.PositiveInfinity : Math.Max(0, Math.Min(340, available));
        _workScroll.MaxHeight = Math.Max(0, Math.Min(575, available - 44));
    }

    private void RenderActivity()
    {
        if (!IsWorkCenter) return;
        _activity.Children.Clear();
        var running = _jobs.Where(job => job.Reply is null).ToArray();
        if (running.Length == 0 && !_boardCards.Any(c => c.Status != "done")) _activity.Children.Add(Ui.Text("目前沒有進行中的任務", 13, Ui.Subtle));
        foreach (var job in running) _activity.Children.Add(job.Card);
        RenderBoardStatus();
        if (_kanbanStatus.Text.Length > 0) _activity.Children.Add(Ui.Text(_kanbanStatus.Text, 12, Ui.Subtle));
    }
    internal static bool ShouldSubmitEnter(Key key,bool composing,bool commitPending) => key==Key.Enter && !composing && !commitPending;
    private bool OwnsRequest(CancellationTokenSource session) => ReferenceEquals(_request,session) && !session.IsCancellationRequested && !_app.IsExiting;
    private static Button IconButton(string glyph,string label,Action action)
    {
        var button=new Button { Content=glyph, FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=18,
            Foreground=Brushes.White, Background=Brushes.Transparent, BorderThickness=new Thickness(0),
            Cursor=Cursors.Hand, ToolTip=label, Width=36,Height=36 };
        System.Windows.Automation.AutomationProperties.SetName(button,label);
        if(label=="傳送")button.Content=new System.Windows.Shapes.Path {
            Data=Geometry.Parse("M 2,10 L 21,2 L 13,21 L 10,13 Z M 10,13 L 21,2"),
            Stroke=Brushes.White,StrokeThickness=1.6,Width=22,Height=22,Stretch=Stretch.Uniform };
        var border=new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty,new CornerRadius(18));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center); border.AppendChild(presenter);
        var template=new ControlTemplate(typeof(Button)) { VisualTree=border };
        var hover=new Trigger { Property=IsMouseOverProperty,Value=true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromArgb(45,255,255,255))));
        template.Triggers.Add(hover);button.Template=template;button.Click+=(_,_)=>action();return button;
    }
    public void Capture(bool window)=>_=CaptureAsync(window);
    public void RemoveImage()=>ClearAttachment();
    internal void DraftTask(string text)
    {
        if (_input.Text.Length > 0)
        {
            _status.Text = "輸入欄已有草稿，請先傳送或清空，再開啟擴充安裝任務。";
            return;
        }
        _input.Text = text;
        _input.CaretIndex = text.Length;
    }
    internal async Task ClearHermesMemoryAsync()
    {
        if (_clearingConversation) return;
        var hermes = _app.Hermes ?? throw new InvalidOperationException("請透過米亞 3.2 啟動捷徑連線至 Hermes。");
        if (_jobs.Count > 0 || _dispatches.Any(task => !task.IsCompleted)) throw new InvalidOperationException("請先等待工作結束，或停止工作後再清除。");
        _clearingConversation = true;
        CancelReply();
        try
        {
            await hermes.ClearMemoryAsync();
            _knownTasks.Clear();
            SaveTaskContext();
            _taskResults.ClearConversationText();
            _taskResults.Save();
            _messages.Children.Clear();
            _lastReply = "";
            _hermesFinalReply = null;
            _hermesToolEvents.Clear();
            RenderActivity();
            _status.Text = "已清除 Hermes 記憶與對話紀錄。";
        }
        finally { _clearingConversation = false; }
    }
    private TextBox AddMessage(string who,string text,bool pet)
    {
        if (pet) text = Mia.Services.SpeechTags.Display(text);
        var block=new StackPanel(); block.Children.Add(Ui.Text(who,12,pet?Ui.Gold:Ui.Subtle));
        var body=new TextBox { Text=text, IsReadOnly=true, TextWrapping=TextWrapping.Wrap, BorderThickness=new Thickness(0), Background=Brushes.Transparent, Foreground=Ui.Ink, FontSize=14 };
        block.Children.Add(body);
        _messages.Children.Add(new Border { Child=block, Padding=new Thickness(14), Background=Ui.Panel, CornerRadius=new CornerRadius(10), Margin=new Thickness(0,0,0,10) });
        while(_messages.Children.Count>40)_messages.Children.RemoveAt(0);
        _details.Visibility=Visibility.Visible; _scroll.ScrollToEnd();
        if (pet && text.Length > 0) AddSourceLinks(body, text);
        return body;
    }
    private Task SendAsync()
    {
        if (_clearingConversation) return Task.CompletedTask;
        if (_app.HermesConfigurationError is { } error)
        {
            _status.Text = error;
            return Task.CompletedTask;
        }
        if (_app.Hermes is null && _fileAttachments.Count > 0) { _status.Text = "請透過啟動米亞.vbs 啟動 Hermes，才能處理檔案附件。"; return Task.CompletedTask; }
        return _app.Hermes is not null ? SendJobAsync() : SendCoreAsync();
    }

    private async Task SendCoreAsync()
    {
        string text=_input.Text.Trim(); if(string.IsNullOrEmpty(text)||_capturing||_composing||_commitPending||_app.IsExiting)return;
        CancelReply();
        _hermesToolEvents.Clear();
        RenderActivity();
        var images=_attachments.Select(a=>a.Bytes).ToArray();
        var times=string.Join("、",_attachments.Select((a,i)=>$"圖{i+1}：{a.Time:HH:mm:ss}"));
        ClearAttachment();
        var session=new CancellationTokenSource(); _request=session;
        _input.Clear(); AddMessage("你",text+(images.Length>0?$"\n〔附有 {images.Length} 張畫面 · {times}〕":""),false);
        _status.Text="米亞正在思考…"; _app.Pet.SetMood("thinking");
        var presentation = new ReplyPresentation(this, "米亞", session.Token, SpeechTags.RequestsUrls(text));
        var streamed = new System.Text.StringBuilder();
        try
        {
            string prompt = text+(images.Length>0?$"\n附圖是先前選取的獨立快照，擷取時間：{times}，不代表現在的畫面。":"");
            string reply = await _app.Ai.ReplyWithImagesAsync(prompt, images, session.Token, delta =>
            {
                if (!OwnsRequest(session)) return;
                streamed.Append(delta);
                presentation.Update(streamed.ToString());
            });
            if (!OwnsRequest(session)) return;

            _lastReply = reply.Split("\n來源：")[0];
            await presentation.CompleteAsync(reply, _lastReply);
            if (OwnsRequest(session))
            {
                _status.Text = presentation.VoiceError ?? _app.Ai.MemoryWarning ?? _app.Ai.RoleWarning ?? "回答完成。";
                _app.Pet.SetMood("idle");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_request,session) && !_app.IsExiting) _status.Text = "已停止。";
            session.Cancel();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_request,session)) { _status.Text = Friendly(ex); _app.Pet.SetMood("error"); }
            session.Cancel();
        }
        finally
        {
            await presentation.AbortAsync();
            if (ReferenceEquals(_request,session))
            {
                _request = null;
                _pendingControls.Clear(); _controls.Children.Clear();
                if (session.IsCancellationRequested) _app.Pet.SetMood("idle");
            }
            session.Dispose();
        }
    }
    private Task HandleHermesUpdateAsync(CancellationTokenSource session, HermesUpdate update, Action<string>? onDelta)
    {
        if (!ReferenceEquals(_request, session) || _app.IsExiting) return Task.CompletedTask;
        if (update.Event == "run.stop_requested") _status.Text = "已向 Hermes 送出停止要求。";
        if (!OwnsRequest(session)) return Task.CompletedTask;
        switch (update.Event)
        {
            case "message.delta" when update.Text.Length > 0:
                onDelta?.Invoke(update.Text);
                break;
            case "tool.started" or "tool.completed" or "tool.failed":
                if (update.Tool.Length == 0) break;
                if (_hermesToolEvents.Count < 300) _hermesToolEvents.Add(update.Event + " " + update.Tool);
                RenderActivity();
                _status.Text = update.Event switch
                {
                    "tool.started" => $"Hermes 正在使用 {update.Tool}…",
                    "tool.failed" => $"Hermes 工具執行未完成：{update.Tool}",
                    _ => $"Hermes 工具已回傳：{update.Tool}"
                };
                break;
            case "run.queued": _status.Text = "Hermes 工作已排入佇列。"; break;
            case "run.reconnecting": _status.Text = "正在重新取得 Hermes 工作狀態…"; break;
            case "approval.request" or "clarify.request" when update.Control is { } control:
                ShowHermesControl(session, control);
                break;
            case "approval.responded" or "approval.settled" or "clarify.settled":
                _pendingControls.Clear(); _controls.Children.Clear();
                break;
        }
        return Task.CompletedTask;
    }

    private void ShowHermesControl(CancellationTokenSource session, HermesControl control)
    {
        if (_pendingControls.ContainsKey(control)) return;
        bool clarify = control.Kind == "clarify.request";
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        card.Children.Add(Ui.Text(clarify ? "Hermes 需要補充資訊" : "Hermes 請求執行許可", 13, Ui.Gold));
        card.Children.Add(Ui.Text(control.Text.Length == 0 ? "請在 Hermes 檢查這筆要求。" : control.Text, 12));
        _controls.Children.Add(card);
        _pendingControls.Add(control, card);
        if (control.Text.Length == 0) return;
        if (clarify)
        {
            var answer = Ui.Input(); answer.MaxLength = 8000;
            card.Children.Add(answer);
            card.Children.Add(Ui.Button("回覆這個問題", () => _ = RespondAsync(answer.Text.Trim())));
        }
        else
        {
            var actions = new WrapPanel();
            actions.Children.Add(Ui.Button("允許這一次", () => _ = RespondAsync("once")));
            actions.Children.Add(Ui.Button("拒絕", () => _ = RespondAsync("deny")));
            card.Children.Add(actions);
        }

        async Task RespondAsync(string answer)
        {
            if (!OwnsRequest(session) || answer.Length == 0 || !card.IsEnabled) return;
            card.IsEnabled = false;
            try
            {
                await _app.Hermes!.RespondAsync(control, answer, session.Token);
                if (OwnsRequest(session))
                {
                    _controls.Children.Remove(card);
                    _status.Text = "Hermes 已收到回覆，正在繼續工作…";
                }
            }
            catch (OperationCanceledException) when (!OwnsRequest(session)) { }
            catch (Exception ex)
            {
                if (OwnsRequest(session)) { _status.Text = Friendly(ex); card.IsEnabled = true; }
            }
        }
    }

    internal Task WaitForRequestAsync() => Task.WhenAll(_jobs.Select(job => job.Execution).Concat(_dispatches).ToArray());
    internal async Task<HermesUiCheck> VerifyHermesAsync(string prompt)
    {
        _hermesToolEvents.Clear();
        _hermesFinalReply = null;
        _input.Text = prompt;
        await SendAsync();
        string reply = _hermesFinalReply ?? "";
        bool visible = reply.Length > 0 && _messages.Children.OfType<Border>().Any(border =>
            border.Child is StackPanel panel && panel.Children.OfType<TextBox>().Any(box => box.Text == SpeechTags.Display(reply)));
        return new(visible, "Hermes", reply, _status.Text, _hermesToolEvents.ToArray());
    }
    internal sealed record HermesUiCheck(bool Success, string Backend, string Reply, string Status, string[] ToolEvents);
    private static void AddSourceLinks(TextBox body, string reply)
    {
        if (body.Parent is not StackPanel panel) return;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(reply, @"https?://[^\s<>]+"))
        {
            string url = match.Value.TrimEnd(')', '）', '。', ']', ',');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) continue;
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(uri.Host)) { NavigateUri = uri };
            link.RequestNavigate += (_, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { MessageBox.Show("無法開啟瀏覽器，請複製來源網址。"); }
                e.Handled = true;
            };
            var label = new TextBlock { Margin = new Thickness(0,4,0,0) }; label.Inlines.Add(link); panel.Children.Add(label);
        }
    }
    public void CancelReply()
    {
        foreach (var job in _jobs.ToArray()) StopJob(job);
        _routingCancellation.Cancel();
        _routingCancellation.Dispose();
        _routingCancellation = new();
        if (_app.Hermes is not null && !_hermesRequest.IsCompleted && _request is { } running)
        {
            running.Cancel(); _app.Voice.Stop();
            if (!_app.IsExiting) _status.Text="正在通知 Hermes 停止工作…";
            return;
        }
        var previous=_request; _request=null;
        previous?.Cancel(); _captureCancellation?.Cancel(); _app.Voice.Stop();
        if(!_app.IsExiting) { _status.Text="已停止。"; _app.Pet.SetMood("idle"); }
    }
    private async Task CaptureAsync(bool window)
    {
        if(_capturing||_app.IsExiting)return;
        if(_attachments.Count>=6){_status.Text="一次最多六張附圖，請先移除或傳送。";return;}
        if (_app.Hermes is null) CancelReply();
        _capturing=true;
        bool visible=_app.Pet.IsVisible;
        using var session=new CancellationTokenSource(); _captureCancellation=session;
        try
        {
            Hide(); _app.Pet.Hide();
            await Task.Delay(200,session.Token);
            if(_app.IsExiting)return;
            var bytes=window?await CaptureService.CaptureWindowAsync(session.Token):await CaptureService.CaptureRegionAsync(session.Token);
            if(bytes!=null && !session.IsCancellationRequested && !_app.IsExiting)
            { Attach(bytes); _status.Text=_app.Hermes is not null ? "檢查附圖後按傳送，才會交給 Hermes。" : "檢查附圖後按傳送，才會交給 OpenAI。"; }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!_app.IsExiting)_status.Text=Friendly(ex); }
        finally
        {
            _captureCancellation=null; _capturing=false;
            if(!_app.IsExiting)
            {
                // Restore the owner first; WPF can otherwise hide its just-shown owned pane.
                if(visible)_app.Pet.Show();
                Show(); Activate();
            }
        }
    }
    private void Attach(byte[] bytes)
    {
        if(_attachments.Count>=6 || _attachments.Sum(a=>(long)a.Bytes.Length)+bytes.Length>24*1024*1024)
            throw new InvalidOperationException("一次最多六張附圖，合計不可超過 24 MB。");
        _attachments.Add(new Attachment(bytes,DateTime.Now)); RenderAttachments();
        _details.Visibility=Visibility.Visible;
    }
    private void RenderAttachments()
    {
        _previews.Children.Clear();
        foreach(var item in _attachments)
        {
            using var stream=new MemoryStream(item.Bytes);var bitmap=new BitmapImage();
            bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
            var tile=new Grid { Width=150,Height=108,Margin=new Thickness(0,4,8,4) };
            tile.Children.Add(new Image { Source=bitmap,Stretch=Stretch.Uniform,ToolTip=$"擷取時間 {item.Time:HH:mm:ss}" });
            var remove=IconButton("\uE711","移除此附圖",()=>{_attachments.Remove(item);RenderAttachments();});
            remove.Width=28;remove.Height=28;remove.FontSize=13;
            remove.HorizontalAlignment=HorizontalAlignment.Right;remove.VerticalAlignment=VerticalAlignment.Bottom;
            remove.Background=new SolidColorBrush(Color.FromArgb(230,45,45,45));tile.Children.Add(remove);_previews.Children.Add(tile);
        }
        foreach (string path in _fileAttachments)
        {
            var tile = new DockPanel { Margin = new Thickness(0, 4, 8, 4), MaxWidth = 300, ToolTip = path };
            var remove = IconButton("\uE711", "移除此檔案", () => { _fileAttachments.Remove(path); RenderAttachments(); });
            remove.Width = 28; remove.Height = 28; DockPanel.SetDock(remove, Dock.Right);
            tile.Children.Add(remove);
            tile.Children.Add(Ui.Text(Path.GetFileName(path), 12));
            _previews.Children.Add(tile);
        }
        _attachmentLabel.Text = _attachments.Count + _fileAttachments.Count == 0 ? "" :
            $"待傳送 {_fileAttachments.Count} 個檔案、{_attachments.Count} 張快照 · 與文字一起送出";
    }
    private void ClearAttachment() { _attachments.Clear(); _fileAttachments.Clear(); RenderAttachments(); }
    private void AttachFiles(string[] files)
    {
        var paths = files.Select(FileAttachments.Validate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Union(_fileAttachments, StringComparer.OrdinalIgnoreCase).Count() > 16)
            throw new InvalidOperationException("一次最多附加 16 個檔案。");
        foreach (string path in paths)
            if (!_fileAttachments.Contains(path, StringComparer.OrdinalIgnoreCase)) _fileAttachments.Add(path);
        RenderAttachments();
        _details.Visibility = Visibility.Visible;
        _status.Text = "檔案已加入；輸入需求後傳送。";
    }
    internal bool VerifyAttachmentControls()
    {
        ClearAttachment();
        foreach(var color in new[]{Colors.SteelBlue,Colors.DarkSeaGreen,Colors.SlateBlue})
        {
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawRectangle(new SolidColorBrush(color),null,new Rect(0,0,220,120));
            var bitmap=new RenderTargetBitmap(220,120,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();png.Save(stream);Attach(stream.ToArray());
        }
        var first=_attachments[0];var last=_attachments[2];
        var middle=(Grid)_previews.Children[1];
        var remove=(Button)middle.Children[1];remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _attachments.Count==2 && ReferenceEquals(_attachments[0],first) && ReferenceEquals(_attachments[1],last) && _previews.Children.Count==2;
    }
    internal bool VerifyFileAttachmentControls()
    {
        ClearAttachment();
        string first = Path.Combine(SettingsStore.DataDirectory, "附件.custom");
        string second = Path.Combine(SettingsStore.DataDirectory, "資料.docx");
        File.WriteAllText(first, "synthetic file"); File.WriteAllText(second, "synthetic file");
        AttachFiles([first, second, first]);
        bool accepted = _fileAttachments.Count == 2 && _previews.Children.Count == 2;
        var tile = (DockPanel)_previews.Children[0];
        ((Button)tile.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool removed = _fileAttachments.SequenceEqual([second]) && _previews.Children.Count == 1;
        ClearAttachment();
        bool cleared = _fileAttachments.Count == 0 && _previews.Children.Count == 0 && File.Exists(first) && File.Exists(second);
        AttachFiles([first, second]);
        return accepted && removed && cleared;
    }
    private void OnDrop(object sender,DragEventArgs e)
    {
        try
        {
            if(e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length>0)
            {
                AttachFiles(files);
            }
            else if(e.Data.GetData(DataFormats.UnicodeText) is string text)_input.Text=text.Length>8000?text[..8000]:text;
        }
        catch(Exception ex) { _status.Text=Friendly(ex); }
        e.Handled=true;
    }
    internal static string Friendly(Exception ex) => ex is System.Net.Http.HttpRequestException ? "暫時無法連線，請檢查網路後再試。" : ex is InvalidOperationException or ArgumentException or TimeoutException or IOException ? ex.Message : "操作未完成，請重試或檢查設定。";
}
