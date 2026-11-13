using System;
using System.Collections.Generic;
using System.Linq;

namespace Mia;

internal sealed partial class ToyBallWindow
{
    private IntPtr platform,ignoredPlatform;
    private NativeDesktop.Rect previousPlatform;
    private double platformFraction;
    private double platformSeconds;
    private DateTime ignorePlatformUntil;
    private Random platformRandom=new();
    private int surfaceTick;
    private List<NativeDesktop.Surface> ballSurfaces=new();
    internal IntPtr Platform=>platform;
    private void DetachPlatform(){platform=IntPtr.Zero;}
    internal bool AttachToWindow(IntPtr window,double horizontal)
    {
        if(!NativeDesktop.TrySurface(window,out var r)||NativeDesktop.IsZoomed(window))return false;
        double center=x+Radius;
        var actual=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)center,Y=r.Top+8});
        var allowed=pet.AllowedWorkArea(new NativeDesktop.Point {X=(int)center,Y=r.Top+8});
        if(actual.Left!=allowed.Left||actual.Top!=allowed.Top||r.Top-2*Radius<allowed.Top||!NativeDesktop.IsExposed(new NativeDesktop.Surface(window,r),center))return false;
        if(center<r.Left+Radius||center>r.Right-Radius)return false;
        platform=window;previousPlatform=r;platformFraction=(center-r.Left)/r.Width;platformSeconds=0;
        vx=horizontal;vy=0;y=r.Top-2*Radius;NativeDesktop.Position(handle,x,y);return true;
    }
    private bool UpdatePlatform(double dt,bool paused)
    {
        if(platform==IntPtr.Zero)return false;
        NativeDesktop.GetWindowRect(handle,out var b);
        if(!NativeDesktop.TrySurface(platform,out var r)||NativeDesktop.IsZoomed(platform))
        {DetachPlatform();vy=0;return false;}
        double center=r.Left+r.Width*platformFraction;
        var actual=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)center,Y=r.Top+8});
        var allowed=pet.AllowedWorkArea(new NativeDesktop.Point {X=(int)center,Y=r.Top+8});
        if(actual.Left!=allowed.Left||actual.Top!=allowed.Top||r.Top-b.Height<allowed.Top||!NativeDesktop.IsExposed(new NativeDesktop.Surface(platform,r),center))
        {DetachPlatform();vy=0;ConstrainToScreens();return false;}
        PreviousCenterX=center;PreviousBottom=r.Top;
        if(!paused)
        {
            platformSeconds+=dt;vx=WindowBallRules.RollSpeed(vx,dt,PhysicsScale);
            if(platformSeconds>10)vx=(center<r.Left+r.Width/2.0?-1:1)*Math.Max(Math.Abs(vx),140*PhysicsScale);
            center+=vx*dt;
        }
        x=center-b.Width/2.0;y=r.Top-b.Height;previousPlatform=r;
        if(center<r.Left+b.Width*.2||center>r.Right-b.Width*.2)
        {ignoredPlatform=platform;ignorePlatformUntil=DateTime.UtcNow.AddSeconds(.6);DetachPlatform();vy=30*PhysicsScale;}
        else platformFraction=(center-r.Left)/r.Width;
        if(x<allowed.Left||x+b.Width>allowed.Right)
        {DetachPlatform();x=Math.Clamp(x,allowed.Left,Math.Max(allowed.Left,allowed.Right-b.Width));vx=-vx*.8;vy=30*PhysicsScale;}
        NativeDesktop.Position(handle,x,y);return true;
    }
    private void TryLandOnWindow(double oldX,double oldBottom,double width,double height)
    {
        if(vy<=0)return;
        if(surfaceTick++%8==0)ballSurfaces=NativeDesktop.Surfaces();
        foreach(var candidate in ballSurfaces.OrderBy(s=>s.Bounds.Top))
        {
            if(candidate.Handle==ignoredPlatform&&DateTime.UtcNow<ignorePlatformUntil)continue;
            if(!NativeDesktop.TrySurface(candidate.Handle,out var r)||NativeDesktop.IsZoomed(candidate.Handle))continue;
            if(!WindowBallRules.CrossesTop(oldBottom,y+height,vy,r.Top))continue;
            double contact=WindowBallRules.ContactX(oldX,x+width/2,oldBottom,y+height,r.Top);
            if(contact<r.Left+width*.5||contact>r.Right-width*.5||x+width/2<r.Left+width*.5||x+width/2>r.Right-width*.5)continue;
            if(!NativeDesktop.IsExposed(new NativeDesktop.Surface(candidate.Handle,r),contact))continue;
            if(!WindowBallRules.Settles(platformRandom.NextDouble()))continue;
            if(AttachToWindow(candidate.Handle,Math.Abs(vx)<25*PhysicsScale?(platformRandom.Next(2)==0?-1:1)*45*PhysicsScale:vx*.6))break;
        }
    }
    internal bool TossToWindow(NativeDesktop.Surface target,double targetX,NativeDesktop.Rect screen)
    {
        if(!PetHeld)return false;
        var launch=WindowBallRules.ThrowToTop(CenterX,CenterY,targetX,target.Bounds.Top,Radius,screen.Top,900*PhysicsScale,PhysicsScale);
        if(launch is not {} speed)return false;
        PetHeld=false;DetachPlatform();vx=speed.X;vy=speed.Y;ignoredPlatform=IntPtr.Zero;return true;
    }
    internal void TossDownFromWindow(IntPtr window,double direction,double strength)
    {
        if(!PetHeld)return;
        PetHeld=false;DetachPlatform();ignoredPlatform=window;ignorePlatformUntil=DateTime.UtcNow.AddSeconds(1.5);
        vx=direction*(160+120*strength)*PhysicsScale;vy=(100+90*strength)*PhysicsScale;
    }
}
