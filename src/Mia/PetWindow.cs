using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mia;

public sealed partial class PetWindow : Window
{
    private const int TopmostCheckIntervalMs = 150;
    public event Action? ChatRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<string>? ModeChanged;
    public bool InteractionHints { get; set; }
    public bool Paused { get; set; }
    private bool interactionOpen;
    public bool InteractionOpen
    {
        get=>interactionOpen;
        set {if(interactionOpen==value)return;interactionOpen=value;MarkInteraction();velocityX=0;}
    }
    public string EffectiveMode=>mode;
    public bool IsDocked=>perch!=IntPtr.Zero;
    internal IntPtr DockedWindow=>perch;
    internal string CurrentPoseName=>CurrentAnimation;
    public string CurrentAnimation { get; private set; }="idle";
    public IReadOnlyList<string> AvailableInteractionAnimations=>animations.Names;
    public FocusClock FocusTimer { get; }=new();
    private ToyBallWindow? ball;
    private DockHintWindow? focusHint;
    private string autonomous="idle";
    private DateTime actionUntil,nextKick,headUntil,nextHead,interactionUntil;
    public string BehaviorStatus=>FocusTimer.Completed?"專注完成，休息一下":FocusTimer.Running?$"專注剩餘 {TimeSpan.FromSeconds(FocusTimer.RemainingSeconds).TotalMinutes:0} 分鐘":autonomous;
    public void StartFocus(int minutes){SetMode("companion");FocusTimer.Start(minutes);lastFocusTick=DateTime.UtcNow;}
    public void StopFocus(){FocusTimer.Stop();focusHint?.Hide();}
    public void ToggleBall()
    {
        if(ball!=null){CancelBallPlay();ball.Close();velocityX=0;actionUntil=DateTime.MinValue;return;}
        MarkInteraction();
        NativeDesktop.GetWindowRect(hwnd,out var body);
        double ballX=x+body.Width*.65,ballY=perch==IntPtr.Zero?y:previousPerch.Top-42*body.Width/192.0;
        ball=new ToyBallWindow(this,ballX,ballY);ball.Closed+=(_,_)=>ball=null;ball.Show();
        if(perch!=IntPtr.Zero)ball.AttachToWindow(perch,35*body.Width/192.0);
    }
    private readonly Image sprite=new() { Stretch=Stretch.Fill };
    private readonly ScaleTransform expressionScale=new(1,1);
    private readonly ScaleTransform kickFacing=new(1,1);
    private DateTime kickStarted,kickUntil;
    private double kickDirection=1,pickupX,pickupY,walkingDistance;
    private bool kickApplied;
    private int ballPlayKind,playCycles;
    private DateTime nextSpeechPose;
    private bool speechGesture;
    private SpeechAnimation speechAnimation = SpeechAnimation.Default;
    private SpeechAnimation voiceAnimation = SpeechAnimation.Default;
    private bool voiceSpeaking;
    private readonly Dictionary<Guid, SpeechAnimation> textReplies = new();
    private bool processingTasks;
    private bool WorkingInCompanion => processingTasks && mode == "companion";
    // ACT-008 is the thinking row in the original sprite sheet. Its lifetime follows jobs, not a mood timeout.
    public void SetTaskProcessing(bool active)
    {
        if (active && !processingTasks) MarkInteraction();
        processingTasks = active;
    }
    internal void SetTextReply(Guid id, string? text)
    {
        if (text is null) textReplies.Remove(id);
        else textReplies[id] = SpeechAnimation.Resolve(text);
        RefreshSpeaking();
    }
    private void RefreshSpeaking()
    {
        bool active = voiceSpeaking || textReplies.Count > 0;
        if (active && !speaking) MarkInteraction();
        speaking = active;
        var animation = voiceSpeaking ? voiceAnimation : textReplies.Values.LastOrDefault() ?? SpeechAnimation.Default;
        // An untagged response still uses the authored EMO-028 atlas.
        if (active && animation == SpeechAnimation.Default) animation = SpeechAnimation.All.First(p => p.Tag == "peaceful");
        if (!active) animation = SpeechAnimation.Default;
        if (speechAnimation != animation) { speechAnimation = animation; speechAnimationStarted = DateTime.UtcNow; }
    }
    private DateTime speechAnimationStarted;
    public void SetSpeechAnimation(SpeechAnimation animation)
    {
        voiceAnimation = animation;
        RefreshSpeaking();
    }
    private double speechTilt;
    private string BallPlayName=>ballPlayKind switch {1=>"試探輕撥",2=>"蹲伏撲球",3=>"拿球拋高",4=>"抱球散步",5=>"抱球喘口氣",_=>"踢球"};
    private readonly RotateTransform expressionTilt=new();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<(int Row,int Column),PetAnimationBank.Frame> frames=new();
    private readonly int[] counts={6,8,8,4,5,8,6,6,6};
    private readonly Random random=new();
    private readonly PetAnimationBank animations;
    private PetAnimationBank.Frame currentFrame=null!;
    private DockHintWindow? dockHint;
    private NativeDesktop.Surface? dockCandidate;
    private IntPtr hwnd,perch;
    private readonly WindowShakeDetector windowShake=new();
    private DateTime noDockUntil, invalidPerchSince;
    private NativeDesktop.Rect previousPerch;
    private double x,y,velocityX,velocityY,dragOffsetX,dragOffsetY,perchFraction=.5;
    private bool dragging,dragMoved,grounded,initialized,stopped,listening,speaking;
    private NativeDesktop.Point pressPoint,previousCursor;
    private string configuredMode="companion",mood="idle";
    private string mode=>InteractionOpen?"companion":configuredMode;
    private DateTime lastTick=DateTime.UtcNow,nextWander,gazeUntil,touchCooldown,landingAt,pettedAt,moodUntil,animationStarted=DateTime.UtcNow;
    private DateTime lastInteraction=DateTime.UtcNow,stretchUntil;
    private DateTime lastFocusTick=DateTime.UtcNow;
    private int tickIndex;
    private long nextTopmostCheck;
    private double pettingDistance;
    private Point lastTouch;
    private HwndSource? source;
    private List<NativeDesktop.Surface> surfaces=new();

    public PetWindow()
    {
        Title="米亞 MIA";Width=192;Height=208;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowInTaskbar=false;ShowActivated=false;Topmost=true;Content=sprite;
        sprite.RenderTransformOrigin=new Point(.5,.94);
        var transform=new TransformGroup();transform.Children.Add(kickFacing);transform.Children.Add(expressionScale);transform.Children.Add(expressionTilt);sprite.RenderTransform=transform;

        RenderOptions.SetBitmapScalingMode(sprite,BitmapScalingMode.HighQuality);
        animations=new PetAnimationBank(Path.Combine(AppContext.BaseDirectory,"Assets","Interactions"));
        LoadFrames();ShowFrame(frames[(0,0)]);
        MouseRightButtonUp+=(_,e)=>{MarkInteraction();ChatRequested?.Invoke();e.Handled=true;};
        MouseLeftButtonDown+=OnDown;MouseLeftButtonUp+=OnUp;MouseMove+=OnMove;
        LostMouseCapture+=(_,_)=>{if(dragging)ReleaseDrag();};
        MouseLeave+=(_,_)=>pettingDistance=0;
        SourceInitialized+=(_,_)=>{hwnd=new WindowInteropHelper(this).Handle;NativeDesktop.ConfigurePet(hwnd);source=HwndSource.FromHwnd(hwnd);source?.AddHook(WindowHook);};
        Loaded+=(_,_)=>{initialized=true;ReturnToCursor();SetMood("greeting");lastTick=DateTime.UtcNow;timer.Start();};
        timer.Tick+=Tick;
        IsVisibleChanged+=(_,_)=>
        {
            if(!initialized||stopped)return;
            if(IsVisible){nextTopmostCheck=0;lastTick=DateTime.UtcNow;timer.Start();}
            else {timer.Stop();dockHint?.Hide();if(dragging){ReleaseDrag();ReleaseMouseCapture();}}
        };
        Closed+=(_,_)=>Stop();
    }
    private void LoadFrames()
    {
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
        image.UriSource=new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","spritesheet.png"));image.EndInit();image.Freeze();
        int scale=image.PixelWidth/1536;
        if(scale<1||scale>4||image.PixelWidth!=1536*scale||image.PixelHeight!=2288*scale)throw new InvalidDataException("米亞動畫圖集尺寸不正確，請重新解壓縮完整應用程式。");
        int frameWidth=192*scale,frameHeight=208*scale;
        for(int row=0;row<11;row++)for(int col=0;col<(row<9?counts[row]:8);col++)
        {
            var crop=new CroppedBitmap(image,new Int32Rect(col*frameWidth,row*frameHeight,frameWidth,frameHeight));crop.Freeze();frames[(row,col)]=PetAnimationBank.CreateFrame(crop);
        }
    }
    public void SetMode(string value)
    {
        string selected=BallPlayRules.NormalizeMode(value);if(configuredMode==selected)return;
        configuredMode=selected;StopFocus();MarkInteraction();if(perch!=IntPtr.Zero&&selected=="explore")seatUntil=DateTime.UtcNow.AddSeconds(60);velocityX=0;nextWander=DateTime.UtcNow.AddSeconds(3);ModeChanged?.Invoke(configuredMode);
    }
    internal void CancelBallPlay(){ball?.ReleaseFromPet();kickUntil=DateTime.MinValue;}
    private void MarkInteraction(){timedCompanion.Cancel(DateTime.UtcNow);cursorAttention.Cancel(DateTime.UtcNow);ClearIdleAction(DateTime.UtcNow);interactionUntil=DateTime.UtcNow.AddSeconds(.7);headUntil=DateTime.MinValue;ball?.ReleaseFromPet();kickUntil=DateTime.MinValue;autonomous="idle";actionUntil=DateTime.MinValue;lastInteraction=DateTime.UtcNow;stretchUntil=DateTime.MinValue;}
    public void BeginListening(bool active){if(active&&!listening)MarkInteraction();listening=active;}
    public void SetSpeaking(bool active){if(active && mood=="thinking")mood="idle";voiceSpeaking=active;if(!active)voiceAnimation=SpeechAnimation.Default;RefreshSpeaking();}
    public void OpenSettings()=>SettingsRequested?.Invoke();
    public void RequestExit()=>ExitRequested?.Invoke();
    public void SetMood(string value)
    {
        if(value!="idle")MarkInteraction();
        if(value=="speaking"){SetSpeaking(true);return;}
        mood=value is "thinking" or "waiting" or "happy" or "error" or "greeting"?value:"idle";
        moodUntil=DateTime.UtcNow.AddSeconds(mood is "thinking" or "waiting"?120:3);
    }
    public void ReturnToCursor()
    {
        if(!initialized)return;
        MarkInteraction();
        NativeDesktop.GetCursorPos(out var cursor);var area=AllowedWorkArea(cursor);NativeDesktop.GetWindowRect(hwnd,out var b);
        x=Math.Clamp(cursor.X+24,area.Left,Math.Max(area.Left,area.Right-b.Width));y=area.Bottom-GroundAnchor(b.Height);
        velocityX=velocityY=0;grounded=true;perch=IntPtr.Zero;perchSeated=true;autonomousJump=false;jumpTarget=IntPtr.Zero;dockCandidate=null;dockHint?.Hide();
        NativeDesktop.Position(hwnd,x,y);if(!IsVisible)Show();
        nextTopmostCheck=0;
    }
    public void Stop()
    {
        if(stopped)return;stopped=true;timer.Stop();dockHint?.Close();focusHint?.Close();ball?.Close();
        dragging=false;if(IsMouseCaptured)ReleaseMouseCapture();source?.RemoveHook(WindowHook);
    }
    private static double GroundAnchor(double height)=>height*203/208;
    private double SeatAnchor(double height)=>height*(animations.Get("perched")?.AnchorY??203)/208;
    private void ShowFrame(PetAnimationBank.Frame frame){currentFrame=frame;sprite.Source=frame.Image;}
    private void OnDown(object sender,MouseButtonEventArgs e)
    {
        MarkInteraction();
        if(e.ClickCount==2 && grounded && perch==IntPtr.Zero && !Paused && !speaking && !InteractionOpen)
        {grounded=false;velocityY=-300;noDockUntil=DateTime.UtcNow.AddSeconds(1);e.Handled=true;return;}
        NativeDesktop.GetCursorPos(out var cursor);NativeDesktop.GetWindowRect(hwnd,out var b);x=b.Left;y=b.Top;
        dragOffsetX=cursor.X-x;dragOffsetY=cursor.Y-y;dragging=true;dragMoved=false;pressPoint=cursor;
        velocityX=velocityY=0;dockCandidate=null;CaptureMouse();e.Handled=true;
    }
    private void OnMove(object sender,MouseEventArgs e)
    {
        if(dragging)
        {
            NativeDesktop.GetCursorPos(out var cursor);
            if(!dragMoved)
            {
                if(!InteractionGeometry.HasDragStarted(cursor.X-pressPoint.X,cursor.Y-pressPoint.Y,VisualTreeHelper.GetDpi(this).DpiScaleX))return;
                dragMoved=true;perch=IntPtr.Zero;autonomousJump=false;jumpTarget=IntPtr.Zero;grounded=false;
            }
            double newX=cursor.X-dragOffsetX;velocityX=Math.Clamp((newX-x)*15,-260,260);x=newX;y=cursor.Y-dragOffsetY;
            ClampDragToAllowedScreen();
            NativeDesktop.Position(hwnd,x,y);return;
        }
        var p=e.GetPosition(sprite);
        if(p.Y<Height*.48&&p.X>Width*.18&&p.X<Width*.82)
        {
            pettingDistance+=Math.Min(30,(p-lastTouch).Length);
            if(pettingDistance>95&&DateTime.UtcNow>touchCooldown){PetHead();pettingDistance=0;}
        }else pettingDistance=0;
        lastTouch=p;
    }
    private void PetHead(){MarkInteraction();pettedAt=DateTime.UtcNow;touchCooldown=pettedAt.AddSeconds(2);}
    private void TouchCharacter(Point position)
    {
        if(position.Y<Height*.48 || (perch!=IntPtr.Zero&&perchSeated))PetHead();
        else SetMood("happy");
    }
    private void OnUp(object sender,MouseButtonEventArgs e)
    {
        if(!dragging)return;
        if(!dragMoved)
        {
            dragging=false;dockCandidate=null;dockHint?.Hide();ReleaseMouseCapture();
            TouchCharacter(e.GetPosition(sprite));
        }
        else {ReleaseDrag();ReleaseMouseCapture();}
        e.Handled=true;
    }
    private NativeDesktop.Surface? FindSnapCandidate(double width,double height)
    {
        var area=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)(x+width/2),Y=(int)(y+height/2)});
        double center=x+width/2,tolerance=55*height/208;
        foreach(var s in NativeDesktop.Surfaces())
        {
            area=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)center,Y=s.Bounds.Top+8});
            if(!ScreenAllowed(area))continue;
            if(InteractionGeometry.CanSnap(center,y+GroundAnchor(height),s.Bounds.Left,s.Bounds.Right,s.Bounds.Top,area.Top,SeatAnchor(height),tolerance)
                &&NativeDesktop.IsExposed(s,center))return s;
        }
        return null;
    }
    private void ReleaseDrag()
    {
        bool moved=dragMoved;dragging=false;dragMoved=false;dockCandidate=null;dockHint?.Hide();
        if(!moved)return;
        grounded=false;velocityY=0;perch=IntPtr.Zero;
        NativeDesktop.GetWindowRect(hwnd,out var b);x=b.Left;y=b.Top;
        // Re-query bounds and occlusion at release; a prior hover candidate may have moved/closed.
        if(FindSnapCandidate(b.Width,b.Height) is { } fresh)AttachPerch(fresh,b.Width,b.Height);
    }
    private void AttachPerch(NativeDesktop.Surface surface,double width,double height,bool seated=true)
    {
        windowShake.Reset();invalidPerchSince=default;
        perch=surface.Handle;previousPerch=surface.Bounds;
        perchFraction=Math.Clamp((x+width/2-surface.Bounds.Left)/surface.Bounds.Width,0.05,.95);
        perchSeated=seated;y=surface.Bounds.Top-PerchAnchor(height);Land();
        if(seated)SitOnPlatform(DateTime.UtcNow,height,true);
        else {platformDecisionAt=DateTime.UtcNow.AddSeconds(8+random.Next(9));ChooseExplorationMotion(DateTime.UtcNow,width,false);}
        nextWindowJump=DateTime.UtcNow.AddSeconds(12);NativeDesktop.Position(hwnd,x,y);
    }
    public bool DockToWindow()
    {
        MarkInteraction();
        NativeDesktop.GetWindowRect(hwnd,out var bounds);
        var screen=NativeDesktop.WorkArea(new NativeDesktop.Point {X=bounds.Left+bounds.Width/2,Y=bounds.Top+bounds.Height/2});
        foreach(var s in NativeDesktop.Surfaces())
        {
            var target=NativeDesktop.WorkArea(new NativeDesktop.Point {X=s.Bounds.Left+s.Bounds.Width/2,Y=s.Bounds.Top+8});
            if(target.Left==screen.Left && target.Top==screen.Top && TryDockWindow(s.Handle))return true;
        }
        return false;
    }
    internal bool TryDockWindow(IntPtr handle)
    {
        if(hwnd==IntPtr.Zero || !NativeDesktop.TrySurface(handle,out var r))return false;
        NativeDesktop.GetWindowRect(hwnd,out var b);
        var area=NativeDesktop.WorkArea(new NativeDesktop.Point {X=r.Left+r.Width/2,Y=r.Top+8});
        if(!ScreenAllowed(area))return false;
        var surface=new NativeDesktop.Surface(handle,r);
        if(r.Top-SeatAnchor(b.Height)<area.Top || !NativeDesktop.IsExposed(surface,r.Left+r.Width/2.0))return false;
        x=r.Left+(r.Width-b.Width)/2.0;AttachPerch(surface,b.Width,b.Height);return true;
    }
    private void Tick(object? sender,EventArgs args)
    {
        var now=DateTime.UtcNow;double dt=Math.Clamp((now-lastTick).TotalSeconds,.001,.08);lastTick=now;
        if(!IsVisible||stopped)return;
        EnforceScreenBounds();
        NativeDesktop.GetCursorPos(out var cursor);NativeDesktop.GetWindowRect(hwnd,out var b);
        int width=b.Width,height=b.Height;if(width<=0||height<=0)return;
        long uptime=Environment.TickCount64;
        if(uptime>=nextTopmostCheck)
        {
            nextTopmostCheck=uptime+TopmostCheckIntervalMs;
            NativeDesktop.RestorePetTopmost(hwnd,b);
        }
        x=b.Left;y=b.Top;
        var area=AllowedWorkArea(new NativeDesktop.Point {X=(int)x+width/2,Y=(int)y+height/2});
        FocusTimer.Tick(Math.Max(0,(now-lastFocusTick).TotalSeconds),Paused);lastFocusTick=now;
        if(FocusTimer.Completed)(focusHint??=new DockHintWindow()).Place(x+width/2,y,"專注完成 · 起身伸展，喝點水吧");
        else focusHint?.Hide();
        ball?.Tick(dt,Paused||InteractionOpen);
        if(Paused || InteractionOpen || speaking || WorkingInCompanion || dragging || !grounded || (perch!=IntPtr.Zero&&perchSeated) || ball==null || ball.Held)
            CancelBallPlay();
        if(now<kickUntil && ballPlayKind<3 && !kickApplied && (now-kickStarted).TotalSeconds>=(ballPlayKind==2?.65:ballPlayKind==1?.3:.12))
        {
            kickApplied=true;
            if(ball is { } target && CompanionRules.CanKick(target.CenterX-(x+width/2.0),target.CenterY,y+GroundAnchor(height),width,height,target.Held))target.Push(kickDirection,ballPlayKind);
        }
        if(now<kickUntil && ballPlayKind>=3 && ball is {} carriedBall)
        {
            double elapsed=(now-kickStarted).TotalSeconds;
            if(!kickApplied && elapsed>=.3)
            {
                kickApplied=true;
                if(CompanionRules.CanKick(carriedBall.CenterX-(x+width/2.0),carriedBall.CenterY,y+GroundAnchor(height),width,height,carriedBall.Held)){pickupX=carriedBall.CenterX;pickupY=carriedBall.CenterY;carriedBall.TakeByPet();}
                else CancelBallPlay();
            }
            if(carriedBall.PetHeld && ((ballPlayKind==3 && elapsed>=1.1)||(ballPlayKind==4 && elapsed>=2.7)||(ballPlayKind==5 && elapsed>=3.6)))
            {
                if(ballPlayKind==3)
                {
                    double angle=(random.NextDouble()*2-1)*.85;
                    if(x<area.Left+width)angle=Math.Abs(angle);
                    if(x+width>area.Right-width)angle=-Math.Abs(angle);
                    if(StandingOnWindow&&random.NextDouble()<.45)
                    {double direction=carriedBall.CenterX<previousPerch.Left+previousPerch.Width/2.0?-1:1;carriedBall.TossDownFromWindow(perch,direction,random.NextDouble());autonomous="從窗沿往下丟球";}
                    else if(random.NextDouble()>=.55||!TryThrowOntoWindow(carriedBall,width,height,area))carriedBall.TossFromPet(angle,random.NextDouble(),area);
                }
                else carriedBall.ReleaseFromPet(kickDirection*45,-35);
                kickUntil=now;nextKick=now.AddSeconds(1.2);
            }
        }
        if(now>=kickUntil && ball is { PetHeld:true })ball.ReleaseFromPet();
        tickIndex++;
        ToolTip=InteractionHints?"左鍵點頭／拖曳 · 右鍵切換輸入橫幅、工作中心與收起 · 窗沿可坐下，快速來回晃動會掉落 · 雙擊小跳躍 · 兩種模式皆可放球 · 探索可走跑、短暫停留、跳上窗沿；探索坐下最多一分鐘":null;
        if(tickIndex%8==0)surfaces=NativeDesktop.Surfaces();
        if(Math.Abs(cursor.X-previousCursor.X)+Math.Abs(cursor.Y-previousCursor.Y)>8)gazeUntil=now.AddSeconds(.8);
        previousCursor=cursor;
        if(dragging&&dragMoved)
        {
            if(tickIndex%3==0)dockCandidate=FindSnapCandidate(width,height);
            if(InteractionHints && dockCandidate is { } candidate)(dockHint??=new DockHintWindow()).Place(x+width/2.0,candidate.Bounds.Top,"放開，坐到這個視窗上");
            else dockHint?.Hide();
        }
        else
        {
            dockCandidate=null;
            if(InteractionHints && perch!=IntPtr.Zero&&(now-landingAt).TotalSeconds<3)(dockHint??=new DockHintWindow()).Place(x+width/2.0,previousPerch.Top,"已停靠 · 移動視窗，我會跟著走");
            else dockHint?.Hide();
        }
        if(!dragging)
        {
            if(perch!=IntPtr.Zero)
            {
                bool valid=NativeDesktop.TrySurface(perch,out var current);bool shaken=false;
                if(valid)
                {
                    x=current.Left+current.Width*perchFraction-width/2.0;
                    area=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)x+width/2,Y=current.Top+8});
                    // Once attached, normal movement and transient occlusion do not detach Mia.
                    double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
                    if(!ScreenAllowed(area) || InteractionGeometry.MustLeavePerch(current.Top,PerchAnchor(height),area.Top,NativeDesktop.IsZoomed(perch)) ||
                        windowShake.Sample(current.Left/dpi,current.Top/dpi,now.Ticks/(double)TimeSpan.TicksPerSecond))
                    {valid=false;shaken=true;noDockUntil=now.AddSeconds(1.2);}
                    if(!ScreenAllowed(area))area=AllowedWorkArea(new NativeDesktop.Point {X=(int)x+width/2,Y=current.Top+8});
                }
                if(valid)invalidPerchSince=default;
                else if(!shaken)
                {
                    if(invalidPerchSince==default)invalidPerchSince=now;
                    if((now-invalidPerchSince).TotalMilliseconds<180){valid=true;current=previousPerch;}
                }
                if(valid){y=current.Top-PerchAnchor(height);previousPerch=current;grounded=true;}
                else{perch=IntPtr.Zero;perchSeated=true;grounded=false;velocityY=0;nextWindowJump=now.AddSeconds(12);dockHint?.Hide();}
            }
            UpdateCursorAttention(now,cursor,width,height);
            UpdatePlatformExploration(now,dt,width,height,area);
            UpdateTimedCompanion(now);
            UpdateCompanionIdle(now);
            if(mode=="companion" && !WorkingInCompanion && (ball==null||InteractionOpen) && timedCompanion.Current==null && now>=idleUntil && now>=kickUntil && InteractionGeometry.CanStretch((now-lastInteraction).TotalSeconds,grounded,perch!=IntPtr.Zero,Paused,
                mode,false,speaking,listening&&!InteractionOpen,mood=="idle",animations.Get("stretching")!=null))
            {
                // This measures interaction with Mia only, not operating-system user inactivity.
                stretchUntil=now.AddSeconds(2);lastInteraction=now;idleAction="";idleUntil=DateTime.MinValue;velocityX=0;
            }
            if(grounded&&(perch==IntPtr.Zero||(ball!=null&&StandingOnWindow))&&BallPlayRules.ShouldMove(mode,ball!=null,Paused||InteractionOpen||speaking||WorkingInCompanion||listening||mood!="idle"||now<interactionUntil)&&now>=stretchUntil&&(now-pettedAt).TotalSeconds>1.5)
            {
                if(ball is {Held:false} crossScreen)
                {
                    var destination=crossScreen.Area;
                    if(perch==IntPtr.Zero&&(destination.Left!=area.Left || destination.Top!=area.Top))
                    {
                        x=destination.Left<area.Left?destination.Right-width:destination.Left;
                        area=destination;y=area.Bottom-GroundAnchor(height);
                    }
                }
                if(ball is {Held:true}){velocityX=0;autonomous="看著你手中的球";gazeUntil=now.AddSeconds(.5);}
                else if(now<kickUntil)
                {
                    velocityX=ballPlayKind==4 && ball is {PetHeld:true}?kickDirection*55*(width/192.0):0;
                    if(ballPlayKind==4 && (x<=area.Left+8 || x+width>=area.Right-8))kickDirection=x<=area.Left+8?1:-1;
                    autonomous=BallPlayName;
                }
                else if(ball is { Held:false } toy && toy.Area.Left==area.Left && toy.Area.Top==area.Top)
                {
                    double delta=toy.CenterX-(x+width/2.0);autonomous="追球";
                    bool highBall=toy.Bottom<y+height*.3;
                    double targetDelta=(highBall?toy.TargetForHead(y+height*.2):toy.CenterX)-(x+width/2.0);
                    velocityX=Math.Abs(targetDelta)>width*(highBall?.08:.4)?Math.Sign(targetDelta)*(highBall?300:180)*width/192.0:0;
                    if(highBall)autonomous="追到球下方準備頂球";
                    if(now>nextKick && CompanionRules.CanKick(delta,toy.CenterY,y+GroundAnchor(height),width,height,toy.Held))
                    {ballPlayKind=(ballPlayKind+1+random.Next(5))%6;playCycles++;if(ballPlayKind==5){if(playCycles<3)ballPlayKind=1;else playCycles=0;}kickDirection=ballPlayKind==4?(random.Next(2)==0?-1:1):delta>=0?1:-1;kickStarted=now;kickUntil=now.AddSeconds(ballPlayKind==5?4:ballPlayKind==4?3:ballPlayKind==3?1.4:ballPlayKind==2?1.25:ballPlayKind==1?.95:.5);kickApplied=false;nextKick=now.AddSeconds(ballPlayKind==0?1.1:2.1);velocityX=0;mood="idle";autonomous=BallPlayName;}
                }
                else if(mode=="explore")
                {
                    if(cursorAttention.Active){velocityX=0;autonomous="停下看著游標";}
                    else if(now>=actionUntil||(Math.Abs(velocityX)<1&&idleAction.Length==0))ChooseExplorationMotion(now,width,actionUntil!=DateTime.MinValue);
                    if(now<idleUntil)velocityX=0;
                    else if(Math.Abs(velocityX)>1)autonomous=Math.Abs(velocityX)*192/width>150?"探索跑步":"探索散步";
                }
                else {velocityX=0;autonomous="原地陪伴";}

            }
            else if(grounded&&perch==IntPtr.Zero){velocityX=0;if(ball==null&&now>=idleUntil&&timedCompanion.Current==null)autonomous=mode=="companion"?"原地陪伴":"互動中";}
            FollowBallAcrossWindows(now,width,height,area);
            TryBeginWindowJump(now,width,height,area);
            StepWindowBallPlay(dt,width,height);
            if(perch==IntPtr.Zero)x+=velocityX*dt;
            if(!grounded)
            {
                double oldFeet=y+GroundAnchor(height),oldVelocity=velocityY;velocityY=Math.Min(750,velocityY+1300*dt);y+=(oldVelocity+velocityY)*.5*dt;
                foreach(var s in surfaces)
                {
                    if(now<noDockUntil)break;
                    if(autonomousJump&&s.Handle!=jumpTarget)continue;
                    if(!NativeDesktop.TrySurface(s.Handle,out var r))continue;
                    var fresh=new NativeDesktop.Surface(s.Handle,r);
                    var targetArea=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)(x+width/2.0),Y=r.Top+8});
                    if(!ScreenAllowed(targetArea))continue;
                    if(oldFeet<=r.Top+1&&y+GroundAnchor(height)>=r.Top&&r.Top-(autonomousJump?GroundAnchor(height):SeatAnchor(height))>=targetArea.Top&&
                        x+width/2.0>r.Left+12&&x+width/2.0<r.Right-12&&NativeDesktop.IsExposed(fresh,x+width/2.0))
                    {AttachPerch(fresh,width,height,!autonomousJump);break;}
                }
            }
            if(grounded&&perch==IntPtr.Zero)y=area.Bottom-GroundAnchor(height);
            if(perch==IntPtr.Zero&&y+GroundAnchor(height)>=area.Bottom)
            {y=area.Bottom-GroundAnchor(height);if(!grounded)Land();velocityY=0;}
            var clamped=InteractionGeometry.ClampToWorkArea(x,y,width,perch!=IntPtr.Zero?height:GroundAnchor(height),area.Left,area.Top,area.Right,area.Bottom);
            bool crossed=false;
            if(x!=clamped.X && mode=="explore" && ball==null && grounded && perch==IntPtr.Zero && Math.Abs(velocityX)>1)
            {
                foreach(var adjacent in AllowedScreens.Select(s=>s.Work))
                {
                    bool touches=velocityX<0?Math.Abs(adjacent.Right-area.Left)<4:Math.Abs(adjacent.Left-area.Right)<4;
                    if(touches && Math.Min(adjacent.Bottom,area.Bottom)-Math.Max(adjacent.Top,area.Top)>Math.Min(height,100))
                    {
                        x=velocityX<0?adjacent.Right-width:adjacent.Left;y=adjacent.Bottom-GroundAnchor(height);area=adjacent;crossed=true;break;
                    }
                }
            }
            if(!crossed)
            {
                if(x!=clamped.X){velocityX=-velocityX;if(actionUntil<now.AddSeconds(2))actionUntil=now.AddSeconds(2);nextWander=now.AddSeconds(3);}
                x=clamped.X;y=clamped.Y;
            }
            NativeDesktop.Position(hwnd,x,y);
        }
        if(ball is { Held:false,PetHeld:false } airborne && grounded && (perch==IntPtr.Zero||StandingOnWindow) && !Paused && !InteractionOpen && !speaking && !listening && !dragging && mood!="thinking" && now>=interactionUntil && now>=nextHead)
        {
            double headY=y+height*.20;
            double fraction=Math.Clamp((headY-airborne.PreviousBottom)/Math.Max(.001,airborne.Bottom-airborne.PreviousBottom),0,1);
            double contactX=airborne.PreviousCenterX+(airborne.CenterX-airborne.PreviousCenterX)*fraction;
            if(BallPlayRules.CanHead(airborne.PreviousBottom,airborne.Bottom,headY,contactX-(x+width*.5),width,airborne.VelocityY,false))
            {
                CancelBallPlay();airborne.HeadBounce(headY,(random.NextDouble()*2-1)*.55,random.NextDouble()*.6,area);
                headUntil=now.AddSeconds(.42);nextHead=now.AddSeconds(.65);autonomous="頭頂球";
            }
        }
        if(mood!="idle"&&now>moodUntil)mood="idle";
        if(Paused&&!dragging){SelectPose(now,cursor,width,height);expressionScale.ScaleX=expressionScale.ScaleY=1;expressionTilt.Angle=0;return;}
        SelectPose(now,cursor,width,height);
        if(LocomotionRules.IsWalk(CurrentAnimation))walkingDistance=(walkingDistance+Math.Abs(velocityX)*dt*192/width)%LocomotionRules.WalkCycleDistance;
        UpdateExpression(now,dt);
        if(ball is {PetHeld:true} inHands)
        {
            // Asset hand centers are in logical 192 x 208 space; include facing and body transform.
            double elapsed=(now-kickStarted).TotalSeconds;
            var hand=new Point(93,105);
            if(ballPlayKind==3 && elapsed>=.9)hand=new Point(99,43);
            var local=sprite.TransformToAncestor(this).Transform(new Point(hand.X*sprite.ActualWidth/192,hand.Y*sprite.ActualHeight/208));
            double lift=Math.Clamp((elapsed-.3)/.25,0,1);
            inHands.FollowHands(pickupX+(x+local.X*width/ActualWidth-pickupX)*lift,pickupY+(y+local.Y*height/ActualHeight-pickupY)*lift);
        }
    }
    private void SelectPose(DateTime now,NativeDesktop.Point cursor,double width,double height)
    {
        string pose;
        if(dragging&&dragMoved)pose="carried";
        else if(!grounded&&!dragging)pose=velocityY<0?"jump-up":"falling";
        else if(speaking)pose="speaking";
        else if(WorkingInCompanion)pose="thinking";
        else if(perch!=IntPtr.Zero&&perchSeated)pose=(now-pettedAt).TotalSeconds<1.5?"perched-petting":"perched";
        else if((now-landingAt).TotalSeconds<.4)pose="landing";
        else if((now-pettedAt).TotalSeconds<1.5)pose="petting";
        else if(speaking)pose="speaking";
        else if(now<headUntil)pose="headball";
        else if(now<kickUntil)pose=ballPlayKind switch {1=>"tapping",2=>"pouncing",3=>"tossing",4=>"carryingball",5=>"restingball",_=>"kicking"};
        else if(mood=="thinking")pose="thinking";
        else if(mood is "happy" or "error" or "waiting" or "greeting")pose=mood;
        else if(listening&&!InteractionOpen)pose="listening";
        else if(cursorAttention.Active)pose="gaze";
        else if(!InteractionOpen&&timedCompanion.Current is { } activity)pose=activity.Id;
        else if(now<stretchUntil&&mode!="focus")pose="stretching";
        else if(now<idleUntil&&idleAction.Length>0)pose=idleAction;
        else if(Math.Abs(velocityX)>3&&grounded)pose=LocomotionRules.Pose(velocityX,width,CurrentAnimation);
        else if(autonomous=="休息"&&mode=="companion")pose="resting";
        else if(now<gazeUntil)pose="gaze";
        else pose="idle";
        kickFacing.ScaleX=pose=="walk-left"?-1:pose is "kicking" or "tapping" or "pouncing" or "carryingball" or "tossing"?kickDirection:1;
        if(CurrentAnimation!=pose){if(LocomotionRules.IsWalk(pose)&&!LocomotionRules.IsWalk(CurrentAnimation))walkingDistance=0;CurrentAnimation=pose;animationStarted=now;}
        double seconds=Paused?0:(now-animationStarted).TotalSeconds;
        string art=LocomotionRules.IsWalk(pose)?"walking":pose=="resting"?"petting":pose;
        // Keep the seated artwork and contact anchor while animating a touch response.
        if(pose=="perched-petting")art="perched";
        if(pose=="kicking" && (now-kickStarted).TotalSeconds<.12)art="listening";
        if(pose=="tapping")art=seconds<.15?"landing":"pawing";
        if(pose=="pouncing")art=seconds<.65?"landing":"pounce";
        if(pose is "tossing" or "carryingball")art=seconds<.3?"pawing":pose=="tossing"&&seconds>=.9?"ball-toss":"ball-carry";
        if(pose=="restingball")art=seconds<.3?"pawing":"ball-rest";
        // Sparse gestures with varied rests, rather than a fixed whole-body mouth loop.
        if(pose=="speaking")
        {
            if(now>=nextSpeechPose)
            {
                speechGesture=!speechGesture;
                nextSpeechPose=now.AddSeconds(speechGesture?.3+random.NextDouble()*.4:1.0+random.NextDouble()*2.0);
                speechTilt=(random.NextDouble()-.5)*2;
            }
            art=speechGesture?"speaking":"listening";
            if (speechAnimation != SpeechAnimation.Default)
            {
                double cycle = (now - speechAnimationStarted).TotalSeconds % speechAnimation.Period;
                string emotionArt = "speech-" + speechAnimation.Tag;
                art = animations.Get(emotionArt) != null ? emotionArt : cycle < speechAnimation.Period * .45 ? speechAnimation.Gesture : "speaking";
                seconds = (now - speechAnimationStarted).TotalSeconds;
            }
        }
        var clip=animations.Get(art);
        if(clip!=null){ShowFrame(clip.Frames[TimedCompanion.IsActivity(art)?TimedCompanion.Frame(art,(now-timedCompanion.Started).TotalSeconds,clip.Frames.Length):art=="walking"?LocomotionRules.WalkFrame(walkingDistance,clip.Frames.Length):Array.IndexOf(idleActions,art)>=0?ExplorationRules.IdleFrame((now-idleStarted).TotalSeconds,(idleUntil-idleStarted).TotalSeconds,clip.Frames.Length):InteractionGeometry.FrameIndex(seconds,clip.Frames.Length,clip.Fps)]);return;}
        if(LocomotionRules.IsWalk(pose)){kickFacing.ScaleX=1;pose=pose=="walk-left"?"left":"right";}
        if(pose=="gaze")
        {
            double angle=Math.Atan2(cursor.X-(x+width*.5),-(cursor.Y-(y+height*.3)));
            int direction=((int)Math.Round(angle/(Math.PI/8))+16)%16;ShowFrame(frames[(9+direction/8,direction%8)]);return;
        }
        int row=pose switch {"right"=>1,"left"=>2,"carried" or "falling" or "jump-up" or "landing"=>3,"petting" or "greeting"=>4,"error"=>5,"waiting" or "listening"=>6,"thinking"=>7,"speaking" or "happy"=>8,_=>0};
        ShowFrame(frames[(row,row==0?InteractionGeometry.IdleBlinkFrame(seconds,counts[row],8):InteractionGeometry.FrameIndex(seconds,counts[row],8))]);
    }
    private void Land(){autonomousJump=false;jumpTarget=IntPtr.Zero;grounded=true;velocityX=velocityY=0;landingAt=DateTime.UtcNow;}
    private void UpdateExpression(DateTime now,double dt)
    {
        double tilt=0,sx=1,sy=1;
        if(TimedCompanion.IsActivity(CurrentAnimation))
        {double t=(now-timedCompanion.Started).TotalSeconds;sy+=Math.Sin(t*1.4)*.004;tilt=CurrentAnimation=="evening-music"?Math.Sin(t*1.6)*.8:0;}
        if(LocomotionRules.IsWalk(CurrentAnimation))sy=1+Math.Sin(walkingDistance/LocomotionRules.WalkCycleDistance*Math.PI*4)*.003;
        if(Array.IndexOf(idleActions,CurrentAnimation)>=0)
        {double t=(now-idleStarted).TotalSeconds;sy+=Math.Sin(t*2)*.005;tilt=CurrentAnimation=="proud-nod"?Math.Sin(t*3)*1.6:Math.Sin(t*1.7)*.6;}
        if(CurrentAnimation=="headball"){sy=1.015;tilt=Math.Sin((now-animationStarted).TotalSeconds*8)*1.5;}
        if(CurrentAnimation=="kicking"){tilt=-kickDirection*2*Math.Sin(Math.Clamp((now-kickStarted).TotalSeconds/.5,0,1)*Math.PI);}
        if(CurrentAnimation=="tapping"){double t=(now-kickStarted).TotalSeconds;tilt=kickDirection*5*Math.Sin(Math.Clamp(t/.95,0,1)*Math.PI);sy=.98;}
        if(CurrentAnimation=="pouncing"){double t=(now-kickStarted).TotalSeconds;sy=t<.65?.92:1.02;sx=t<.65?1.035:.99;tilt=kickDirection*(t<.65?-2:7)*Math.Sin(Math.Clamp(t/1.25,0,1)*Math.PI);}
        if(CurrentAnimation=="speaking")tilt=speechTilt;
        if(CurrentAnimation=="speaking" && speechAnimation != SpeechAnimation.Default)
        {
            var motion = speechAnimation.Motion((now - speechAnimationStarted).TotalSeconds);
            tilt = motion.Tilt;
            sy = motion.ScaleY;
        }
        if(CurrentAnimation=="restingball")
        {
            double breath=(now-kickStarted).TotalSeconds;
            sy=1+Math.Sin(breath*8)*.012;tilt=Math.Sin(breath*4)*.65;
        }
        if(CurrentAnimation=="resting")sy=.96+Math.Sin(now.TimeOfDay.TotalSeconds*1.4)*.015;
        if(dragging&&dragMoved){tilt=Math.Clamp(-velocityX*.025,-6,6)+Math.Sin(now.TimeOfDay.TotalSeconds*5)*1.2;sx=.97;sy=1.02;}
        else if(CurrentAnimation is "perched" or "perched-petting")
        {
            sy=1+Math.Sin(now.TimeOfDay.TotalSeconds*2.5)*.008;tilt=Math.Sin(now.TimeOfDay.TotalSeconds*1.5)*.7;
            if(CurrentAnimation=="perched-petting")
            {
                double progress=Math.Clamp((now-pettedAt).TotalSeconds/1.5,0,1);
                double envelope=Math.Sin(progress*Math.PI);
                tilt+=4*Math.Sin(progress*Math.PI*2)*envelope;
                sy-=.025*envelope; sx+=.012*envelope;
            }
        }
        else
        {
            double impact=(now-landingAt).TotalSeconds;if(impact>=0&&impact<.36){double pulse=Math.Sin(impact/.36*Math.PI);sx=1+.045*pulse;sy=1-.10*pulse;}
            double touch=(now-pettedAt).TotalSeconds;if(touch>=0&&touch<1.5)tilt=3.5*Math.Sin(touch/1.5*Math.PI*2)*(1-touch/1.5);
            if(CurrentAnimation is "listening" or "speaking")sy+=Math.Sin(now.TimeOfDay.TotalSeconds*3)*.008;
            if(CurrentAnimation=="stretching")
            {double progress=Math.Clamp((now-animationStarted).TotalSeconds/2,0,1);sy+=Math.Sin(progress*Math.PI)*.018;tilt=Math.Sin(progress*Math.PI*2)*1.4;}
        }
        double blend=Math.Min(1,dt*14);expressionScale.ScaleX+=(sx-expressionScale.ScaleX)*blend;expressionScale.ScaleY+=(sy-expressionScale.ScaleY)*blend;expressionTilt.Angle+=(tilt-expressionTilt.Angle)*blend;
        sprite.RenderTransformOrigin=new Point(.5,perch!=IntPtr.Zero&&perchSeated&&(CurrentAnimation is "perched" or "perched-petting")?(animations.Get("perched")?.AnchorY??203)/208:.94);
    }
    private IntPtr WindowHook(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x0021){handled=true;return new IntPtr(3);}
        if(message==0x0084&&!dragging)
        {
            NativeDesktop.GetCursorPos(out var p);NativeDesktop.GetWindowRect(handle,out var r);
            var wp=new Point((p.X-r.Left)*ActualWidth/Math.Max(1,r.Width),(p.Y-r.Top)*ActualHeight/Math.Max(1,r.Height));
            var inverse=sprite.TransformToAncestor(this).Inverse;var local=inverse?.Transform(wp)??wp;
            int px=(int)Math.Floor(local.X*currentFrame.Width/Math.Max(1,sprite.ActualWidth)),py=(int)Math.Floor(local.Y*currentFrame.Height/Math.Max(1,sprite.ActualHeight));
            if(px<0||px>=currentFrame.Width||py<0||py>=currentFrame.Height||currentFrame.Alpha[py*currentFrame.Width+px]<16)
            {handled=true;return new IntPtr(-1);}
        }
        return IntPtr.Zero;
    }
}
