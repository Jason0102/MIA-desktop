using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Mia;

internal sealed partial class ToyBallWindow : Window
{
    private readonly PetWindow pet;
    private IntPtr handle;
    private double PhysicsScale { get { NativeDesktop.GetWindowRect(handle,out var b);return Math.Max(.5,b.Width/42.0); } }
    private bool held;
    private readonly BallThrowTracker throwTracker=new();
    private static double Clock=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
    internal bool PetHeld { get; private set; }
    private double x,y,vx,vy,offsetX,offsetY;
    public bool Held=>held;
    internal double PreviousCenterX { get; private set; }
    internal double PreviousBottom { get; private set; }
    internal double Bottom { get {NativeDesktop.GetWindowRect(handle,out var b);return b.Bottom;} }
    internal double VelocityY=>vy;
    internal double TargetForHead(double headY)=>BallPlayRules.PredictHeadX(CenterX,CenterY,vx,vy,900*PhysicsScale,headY,Radius,Area.Left,Area.Right,650*PhysicsScale);
    internal double VelocityX=>vx;
    internal double Radius { get {NativeDesktop.GetWindowRect(handle,out var b);return b.Height/2.0;} }
    internal void TossFromPet(double angle,double strength,NativeDesktop.Rect screen)
    {
        if(!PetHeld)return;PetHeld=false;DetachPlatform();
        (vx,vy)=BallPlayRules.ThrowVelocity(CenterY-Radius-screen.Top-12*PhysicsScale,900*PhysicsScale,angle,strength);
        vx=Math.Clamp(vx,-600*PhysicsScale,600*PhysicsScale);
    }
    internal void HeadBounce(double headY,double angle,double strength,NativeDesktop.Rect screen)
    {
        if(held||PetHeld)return;
        DetachPlatform();y=headY-2*Radius;NativeDesktop.Position(handle,x,y);
        (vx,vy)=BallPlayRules.ThrowVelocity(headY-2*Radius-screen.Top-12*PhysicsScale,900*PhysicsScale,angle,strength);
        vx=Math.Clamp(vx,-400*PhysicsScale,400*PhysicsScale);
    }
    public double CenterX { get { NativeDesktop.GetWindowRect(handle,out var b);return b.Left+b.Width/2.0; } }
    internal double CenterY { get { NativeDesktop.GetWindowRect(handle,out var b);return b.Top+b.Height/2.0; } }
    public NativeDesktop.Rect Area=>pet.AllowedWorkArea(new NativeDesktop.Point {X=(int)(x+Radius),Y=(int)(y+Radius)});
    internal ToyBallWindow(PetWindow owner,double px,double py)
    {
        pet=owner;Owner=owner;Width=Height=42*(owner.Width/192);WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Content=CreateBallArt();
        ToolTip="快速拖球後放開可拋出；往上甩可拋高，停住再放開會落下；右鍵收起";
        SourceInitialized+=(_,_)=>{handle=new WindowInteropHelper(this).Handle;NativeDesktop.ConfigurePet(handle);};
        Loaded+=(_,_)=>{x=px;y=py;ConstrainToScreens();};
        MouseLeftButtonDown+=(_,e)=>{NativeDesktop.GetCursorPos(out var p);pet.CancelBallPlay();DetachPlatform();PetHeld=false;offsetX=p.X-x;offsetY=p.Y-y;held=true;vx=vy=0;throwTracker.Reset(p.X,p.Y,Clock);pet.MarkBallInput(released:false);CaptureMouse();e.Handled=true;};
        MouseMove+=(_,_)=>{if(!held)return;NativeDesktop.GetCursorPos(out var p);throwTracker.Sample(p.X,p.Y,Clock);x=p.X-offsetX;y=p.Y-offsetY;ConstrainToScreens();};
        MouseLeftButtonUp+=(_,_)=>{if(held){(vx,vy)=throwTracker.Release(Clock,PhysicsScale);held=false;pet.MarkBallInput(released:true,thrown:vx!=0||vy!=0);}ReleaseMouseCapture();};
        LostMouseCapture+=(_,_)=>{if(held){held=false;vx=vy=0;}};
        MouseRightButtonUp+=(_,_)=>Close();
    }
    private static FrameworkElement CreateBallArt()
    {
        // Vector cel shading stays crisp at every pet scale, with a matte white surface.
        var canvas=new Canvas { Width=42,Height=42 };
        var outline=new SolidColorBrush(Color.FromRgb(73,71,73));
        var body=new Ellipse { Width=38,Height=38,Fill=new SolidColorBrush(Color.FromRgb(248,247,240)) };
        Canvas.SetLeft(body,2);Canvas.SetTop(body,2);canvas.Children.Add(body);
        canvas.Children.Add(new Path {
            Data=Geometry.Parse("M 37,11 C 43,23 36,39 22,40 C 13,40 6,34 3,26 C 9,34 22,36 30,28 C 35,23 37,17 37,11 Z"),
            Fill=new SolidColorBrush(Color.FromRgb(204,206,202)),
            Clip=new EllipseGeometry(new Point(21,21),19,19)
        });
        canvas.Children.Add(new Path {
            Data=Geometry.Parse("M 8,19 C 9,12 15,7 22,7"),
            Stroke=new SolidColorBrush(Color.FromRgb(255,255,253)),StrokeThickness=2.4,
            StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round
        });
        var edge=new Ellipse { Width=38,Height=38,Stroke=outline,StrokeThickness=1.5 };
        Canvas.SetLeft(edge,2);Canvas.SetTop(edge,2);canvas.Children.Add(edge);
        return new Viewbox { Stretch=Stretch.Uniform,Child=canvas };
    }
    internal bool TakeByPet(){if(held)return false;DetachPlatform();PetHeld=true;vx=vy=0;return true;}
    internal void FollowHands(double centerX,double centerY)
    {
        if(!PetHeld)return;
        NativeDesktop.GetWindowRect(handle,out var b);x=centerX-b.Width/2.0;y=centerY-b.Height/2.0;
        NativeDesktop.Position(handle,x,y);
    }
    internal void ReleaseFromPet(double horizontal=0,double vertical=0)
    {
        if(!PetHeld)return;PetHeld=false;vx=horizontal*PhysicsScale;vy=vertical*PhysicsScale;
    }
    internal void Kick(double direction)=>Push(direction,0);
    internal void Push(double direction,int kind)
    {
        if(held||PetHeld)return;
        DetachPlatform();
        vx=direction*(kind==1?85:kind==2?180:260)*PhysicsScale;
        vy=(kind==1?-35:kind==2?-135:-230)*PhysicsScale;
    }
    internal void Tick(double dt,bool paused)
    {
        double desired=42*(pet.Width/192);
        if(Math.Abs(Width-desired)>.01){Width=Height=desired;UpdateLayout();}
        if(held||PetHeld){if(!PetHeld)ConstrainToScreens();return;}
        if(UpdatePlatform(dt,paused))return;
        if(paused){ConstrainToScreens();return;}
        NativeDesktop.GetWindowRect(handle,out var b);PreviousBottom=b.Bottom;PreviousCenterX=b.Left+b.Width/2.0;var area=Area;
        double scale=PhysicsScale;
        double oldVelocity=vy;vy=Math.Min(650*scale,vy+900*scale*dt);x+=vx*dt;y+=(oldVelocity+vy)*.5*dt;
        TryLandOnWindow(PreviousCenterX,PreviousBottom,b.Width,b.Height);
        if(y+b.Height>=area.Bottom){y=area.Bottom-b.Height;vy=Math.Abs(vy)>65*scale?-Math.Abs(vy)*.55:0;vx*=Math.Exp(-1.3*dt);}
        if(x<area.Left||x+b.Width>area.Right){vx=-vx*.8;x=Math.Clamp(x,area.Left,Math.Max(area.Left,area.Right-b.Width));}
        if(y<area.Top){y=area.Top;vy=Math.Abs(vy)*.6;}NativeDesktop.Position(handle,x,y);
    }
    private void ConstrainToScreens()
    {
        NativeDesktop.GetWindowRect(handle,out var b);var area=Area;
        (x,y)=InteractionGeometry.ClampToWorkArea(x,y,b.Width,b.Height,area.Left,area.Top,area.Right,area.Bottom);
        NativeDesktop.Position(handle,x,y);
    }
}
