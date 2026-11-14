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

public sealed class ChatWindow : Window
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
    private bool _capturing;
    private bool _composing, _commitPending;
    private CancellationTokenSource? _captureCancellation;
    private readonly ScrollViewer _detailScroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto, MaxHeight=340 };
    private string _lastReply="";
    private bool _readUrls;
    public bool HasActiveReply => _request!=null;

    public ChatWindow(App app)
    {
        _app=app; Title="米亞 MIA 3.2 · 對話"; Width=380; MinWidth=280; MinHeight=56;
        SizeToContent=SizeToContent.Height; Ui.Style(this);
        var root=new StackPanel(); Content=root;
        var details=new StackPanel { Margin=new Thickness(14) };
        if (app.Hermes is not null)
        {
            var workControls = new DockPanel { LastChildFill = true };
            string? workspace = Environment.GetEnvironmentVariable("MIA_CODEX_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(workspace) && Path.IsPathFullyQualified(workspace))
            {
                var results = Ui.Button("成果資料夾", () => OpenWorkspace(workspace));
                DockPanel.SetDock(results, Dock.Right);
                workControls.Children.Add(results);
            }
            workControls.Children.Add(Ui.Text("Hermes 工作模式", 12, Ui.Gold));
            details.Children.Add(workControls);
        }
        _scroll.Content=_messages; _scroll.MaxHeight=170;
        details.Children.Add(_scroll); details.Children.Add(_status);
        details.Children.Add(_controls);
        details.Children.Add(_previews); details.Children.Add(_attachmentLabel);
        _detailScroll.Content=details; _details.Child=_detailScroll; _details.CornerRadius=new CornerRadius(16);
        _details.Background=new SolidColorBrush(Color.FromArgb(205,56,56,56));
        _details.Margin=new Thickness(0,0,0,8); root.Children.Add(_details);
        var capsule=new Border { Height=56, CornerRadius=new CornerRadius(28),
            Background=new SolidColorBrush(Color.FromArgb(170,100,100,100)), Padding=new Thickness(20,8,10,8) };
        var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(38) });
        capsule.Child=row; root.Children.Add(capsule);
        _stop=IconButton("\uE711","停止工作或語音",CancelReply);
        _stop.Visibility=Visibility.Collapsed;_stop.HorizontalAlignment=HorizontalAlignment.Center;
        _stop.Background=new SolidColorBrush(Color.FromArgb(190,65,65,65));root.Children.Add(_stop);
        var stopTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        stopTimer.Tick+=(_,_)=>_stop.Visibility=(_request!=null||!_hermesRequest.IsCompleted||app.Voice.IsSpeaking)?Visibility.Visible:Visibility.Collapsed;
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
                AddMessage("米亞", turn.Assistant, true);
                _lastReply = turn.Assistant;
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
        Closing+=(_,e)=> { if(!_app.IsExiting) { e.Cancel=true; CancelReply(); Hide(); } };
        PreviewMouseRightButtonUp+=(_,e)=>{e.Handled=true;Hide();};
        AllowDrop=true;
        DragOver+=(_,e)=>{ e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)||e.Data.GetDataPresent(DataFormats.UnicodeText)?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true; };
        Drop+=OnDrop;
        FloatingSurface.Attach(this,app.Pet,wrap:false,suppressEscape:()=>_composing||_commitPending);
        IsVisibleChanged+=(_,_)=> { if(IsVisible)Dispatcher.BeginInvoke(new Action(()=> { if(IsVisible && !_app.IsExiting)_input.Focus(); })); else app.Pet.BeginListening(false); };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(MaxHeightProperty,typeof(Window))!
            .AddValueChanged(this,(_,_)=>_detailScroll.MaxHeight=Math.Max(0,Math.Min(340,MaxHeight/_app.Settings.UiScale-64)));
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
    public void Replay()=>_=ReplayAsync();
    public void RemoveImage()=>ClearAttachment();
    public async void ClearConversation()
    {
        if (_clearingConversation) return;
        _clearingConversation = true;
        CancelReply();
        try
        {
            if (_app.Hermes is { } hermes)
            {
                await _hermesRequest;
                await hermes.ClearConversationAsync();
            }
            else _app.Ai.ClearHistory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { _status.Text="無法清除對話。" + Friendly(ex); return; }
        finally { _clearingConversation = false; }
        _messages.Children.Clear();_lastReply="";ClearAttachment();
        _status.Text="";_details.Visibility=Visibility.Collapsed;
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
        if (_clearingConversation || !_hermesRequest.IsCompleted) return Task.CompletedTask;
        if (_app.HermesConfigurationError is { } error)
        {
            _status.Text = error;
            return Task.CompletedTask;
        }
        Task send = SendCoreAsync();
        if (_app.Hermes is not null) _hermesRequest = send;
        return send;
    }

    private async Task SendCoreAsync()
    {
        string text=_input.Text.Trim(); if(string.IsNullOrEmpty(text)||_capturing||_composing||_commitPending||_app.IsExiting)return;
        CancelReply();
        _readUrls = Mia.Services.SpeechTags.RequestsUrls(text);
        var images=_attachments.Select(a=>a.Bytes).ToArray();
        var times=string.Join("、",_attachments.Select((a,i)=>$"圖{i+1}：{a.Time:HH:mm:ss}"));
        ClearAttachment();
        var session=new CancellationTokenSource(); _request=session;
        _input.Clear(); AddMessage("你",text+(images.Length>0?$"\n〔附有 {images.Length} 張畫面 · {times}〕":""),false);
        _status.Text="米亞正在思考…"; _app.Pet.SetMood("thinking");
        var sentences = System.Threading.Channels.Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        bool speak = !_app.Settings.Muted && !string.IsNullOrWhiteSpace(_app.Settings.VoiceId);
        var reveals = new List<Task>();
        var voiceBubbles = new List<TextBox>();
        Task<string?> voice = PlayVoice();
        var pending = new System.Text.StringBuilder();
        var displayed = new System.Text.StringBuilder();
        TextBox? live = null;
        bool received = false;
        try
        {
            string prompt = text+(images.Length>0?$"\n附圖是先前選取的獨立快照，擷取時間：{times}，不代表現在的畫面。":"");
            string reply = _app.Hermes is { } hermes
                ? await hermes.ReplyAsync(prompt, images, session.Token, update => HandleHermesUpdateAsync(session, update, speak ? null : OnDelta))
                : await _app.Ai.ReplyWithImagesAsync(prompt, images, session.Token, OnDelta);
            void OnDelta(string delta)
            {
                if (!OwnsRequest(session)) return;
                received = true;
                if (!speak)
                {
                    live ??= AddMessage("米亞", "", true);
                    displayed.Append(delta);
                    live.Text = Mia.Services.SpeechTags.Display(displayed.ToString(), streaming: true);
                    _scroll.ScrollToEnd();
                    _status.Text = "米亞正在回答…";
                    return;
                }
                pending.Append(delta);
                string buffered = pending.ToString().TrimStart();
                pending.Clear(); pending.Append(buffered);
                var chunks = Mia.Services.VoiceService.ChunkText(buffered);
                int consumed = 0;
                int ready = chunks.Count - 1;
                if (chunks.Count > 0 && ("。？！!?\n".Contains(chunks[^1][^1]) || chunks[^1].Length >= 1000)) ready++;
                for (int i = 0; i < ready; i++)
                {
                    sentences.Writer.TryWrite(chunks[i]);
                    consumed = buffered.IndexOf(chunks[i], consumed, StringComparison.Ordinal) + chunks[i].Length;
                }
                if (consumed > 0) pending.Remove(0, consumed);
            }
            if(!OwnsRequest(session)) return;

            if (speak)
            {
                if (!received) sentences.Writer.TryWrite(reply.Split("\n來源：")[0]);
                else if (pending.Length > 0) sentences.Writer.TryWrite(pending.ToString());
            }
            sentences.Writer.TryComplete();
            _lastReply=reply.Split("\n來源：")[0];
            if (!speak)
            {
                if (live is null) AddMessage("米亞", reply, true);
                else
                {
                    live.Text = Mia.Services.SpeechTags.Display(reply);
                    AddSourceLinks(live, reply);
                }
            }
            string? voiceError = await voice;
            await Task.WhenAll(reveals);
            if (OwnsRequest(session))
            {
                if (speak && (voiceError is not null || voiceBubbles.Count == 0 || _app.Settings.Muted))
                {
                    foreach (var bubble in voiceBubbles)
                        if (bubble.Parent is StackPanel { Parent: Border border }) _messages.Children.Remove(border);
                    AddMessage("米亞", reply, true);
                }
                else if (speak && reply.Length > _lastReply.Length)
                    AddSourceLinks(voiceBubbles[^1], reply[_lastReply.Length..]);
                _hermesFinalReply = _app.Hermes is not null ? reply : null;
                _status.Text = voiceError ?? (_app.Hermes is { } activeHermes
                    ? activeHermes.MemoryWarning ?? "Hermes 工作完成。"
                    : _app.Ai.MemoryWarning ?? _app.Ai.RoleWarning ?? "回答完成。");
                _app.Pet.SetMood("idle");
            }
        }
        catch(OperationCanceledException)
        {
            if(ReferenceEquals(_request,session) && !_app.IsExiting && _app.Hermes is null) _status.Text="已停止。";
            session.Cancel();
        }
        catch(Exception ex) { if(ReferenceEquals(_request,session)) { _status.Text=Friendly(ex); _app.Pet.SetMood("error"); } session.Cancel(); }
        finally
        {
            sentences.Writer.TryComplete();
            await voice;
            await Task.WhenAll(reveals);
            if(ReferenceEquals(_request,session))
            {
                _request=null;
                _pendingControls.Clear(); _controls.Children.Clear();
                if(session.IsCancellationRequested)_app.Pet.SetMood("idle");
            }
            session.Dispose();
        }
        async Task<string?> PlayVoice()
        {
            if (!speak) return null;
            try { await _app.Voice.SpeakStreamAsync(sentences.Reader, session.Token, segment => reveals.Add(RevealSegmentAsync(segment)), _readUrls); return null; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { return null; }
            catch (Exception ex) { return "文字回答完成；" + Friendly(ex); }
        }
        async Task RevealSegmentAsync(string segment)
        {
            if (!OwnsRequest(session)) return;
            string display = Mia.Services.SpeechTags.Display(segment);
            if (string.IsNullOrWhiteSpace(display)) return;
            _status.Text = "正在播放語音…";
            var body = AddMessage("米亞", "", true);
            voiceBubbles.Add(body);
            int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(display);
            try
            {
                for (int count = 1; ; count = Math.Min(count + 3, starts.Length))
                {
                    if (!OwnsRequest(session)) return;
                    body.Text = count == starts.Length ? display : display[..starts[count]];
                    _scroll.ScrollToEnd();
                    if (count == starts.Length) break;
                    await Task.Delay(30, session.Token);
                }
                AddSourceLinks(body, display);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
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

    private void OpenWorkspace(string workspace)
    {
        try
        {
            var jobs = Path.Combine(workspace, "jobs");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.Exists(jobs) ? jobs : workspace) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ArgumentException)
        { _status.Text = "無法開啟 Codex 成果資料夾。"; }
    }

    internal Task WaitForRequestAsync() => _hermesRequest;
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
    private async Task ReplayAsync()
    {
        if(string.IsNullOrWhiteSpace(_lastReply)||_capturing||_app.IsExiting||!_hermesRequest.IsCompleted)return;
        CancelReply(); var session=new CancellationTokenSource(); _request=session;
        try { _status.Text="正在重播…"; await _app.Voice.SpeakAsync(_lastReply,session.Token, readUrls: _readUrls); if(OwnsRequest(session))_status.Text="播放結束。"; }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(ReferenceEquals(_request,session))_status.Text=Friendly(ex); }
        finally { if(ReferenceEquals(_request,session))_request=null; session.Dispose(); }
    }
    private async Task CaptureAsync(bool window)
    {
        if(_capturing||_app.IsExiting||!_hermesRequest.IsCompleted)return;
        if(_attachments.Count>=6){_status.Text="一次最多六張附圖，請先移除或傳送。";return;}
        CancelReply(); _capturing=true;
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
        _attachmentLabel.Text=_attachments.Count==0?"":$"待傳送 {_attachments.Count}/6 張 · 可繼續框選，與文字一起送出";
    }
    private void ClearAttachment() { _attachments.Clear(); RenderAttachments(); }
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
    private void OnDrop(object sender,DragEventArgs e)
    {
        try
        {
            if(e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length>0)
            {
                if(files.Length+_attachments.Count>6)throw new InvalidOperationException("一次最多六張附圖。");
                foreach(string path in files)
                {
                string ext=Path.GetExtension(path).ToLowerInvariant();
                if(ext is not (".png" or ".jpg" or ".jpeg"))throw new InvalidOperationException("請拖入 PNG 或 JPG 圖片。");
                if(new FileInfo(path).Length>10*1024*1024)throw new InvalidOperationException("請使用小於 10 MB 的圖片。");
                using var stream=File.OpenRead(path); var source=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                double scale=Math.Min(1,1600.0/Math.Max(source.PixelWidth,source.PixelHeight));
                BitmapSource scaled=scale<1?new TransformedBitmap(source,new ScaleTransform(scale,scale)):source;
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(scaled)); using var output=new MemoryStream(); encoder.Save(output); Attach(output.ToArray());
                }
            }
            else if(e.Data.GetData(DataFormats.UnicodeText) is string text)_input.Text=text.Length>8000?text[..8000]:text;
        }
        catch(Exception ex) { _status.Text=Friendly(ex); }
        e.Handled=true;
    }
    internal static string Friendly(Exception ex) => ex is System.Net.Http.HttpRequestException ? "暫時無法連線，請檢查網路後再試。" : ex is InvalidOperationException or ArgumentException or TimeoutException or IOException ? ex.Message : "操作未完成，請重試或檢查設定。";
}
