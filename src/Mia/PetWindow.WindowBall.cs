using System;
using System.Linq;

namespace Mia;

public sealed partial class PetWindow
{
    private DateTime nextBallWindowJump;
    private bool StandingOnWindow=>perch!=IntPtr.Zero&&!perchSeated;
    private void PrepareWindowBallPlay(DateTime now,double width,double height,NativeDesktop.Rect area)
    {
        if(!FreeForAutonomy(now)){velocityX=0;return;}
        if(perchSeated)
        {
            if(previousPerch.Top-GroundAnchor(height)<area.Top+12){LeavePlatform(now,width);return;}
            perchSeated=false;y=previousPerch.Top-GroundAnchor(height);idleAction="";idleUntil=DateTime.MinValue;
        }
    }
    private void FollowBallAcrossWindows(DateTime now,double width,double height,NativeDesktop.Rect area)
    {
        if(ball is not {Held:false,PetHeld:false} toy||!grounded||!FreeForAutonomy(now)||now<kickUntil)return;
        if(StandingOnWindow&&(toy.Area.Left!=area.Left||toy.Area.Top!=area.Top))
        {LeavePlatform(now,width);nextBallWindowJump=now.AddSeconds(1.2);autonomous="跳下窗沿繼續追球";return;}
        if(toy.Platform!=IntPtr.Zero&&toy.Platform!=perch&&NativeDesktop.TrySurface(toy.Platform,out var target))
        {
            double landing=Math.Clamp(toy.CenterX,target.Left+width*.55,Math.Max(target.Left+width*.55,target.Right-width*.55));
            var targetArea=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)landing,Y=target.Top+8});
            bool reachable=ScreenAllowed(targetArea)&&targetArea.Left==area.Left&&targetArea.Top==area.Top&&!NativeDesktop.IsZoomed(toy.Platform)&&
                ExplorationRules.CanReach(y+GroundAnchor(height),target.Top,GroundAnchor(height),area.Top,area.Height,target.Width,width);
            var surface=new NativeDesktop.Surface(toy.Platform,target);
            if(reachable&&NativeDesktop.IsExposed(surface,landing)&&NativeDesktop.IsExposed(surface,landing-width*.2)&&NativeDesktop.IsExposed(surface,landing+width*.2))
            {
                double delta=landing-(x+width/2);
                if(Math.Abs(delta)>width*2.5){velocityX=Math.Sign(delta)*180*width/192;autonomous="跑向球所在的窗沿";return;}
                if(now>=nextBallWindowJump)
                {LaunchWindowJump(now,surface,landing,width,height,area);nextBallWindowJump=now.AddSeconds(2.5);autonomous="跳上窗沿追球";}
                return;
            }
        }
        if(StandingOnWindow&&toy.Bottom>previousPerch.Top+height*.3&&now>=nextBallWindowJump)
        {
            double direction=Math.Sign(toy.CenterX-(x+width/2));if(direction==0)direction=random.Next(2)==0?-1:1;
            LeavePlatform(now,width);velocityX=direction*135*width/192;nextBallWindowJump=now.AddSeconds(1.2);autonomous="跳下去追球";
        }
    }
    private void StepWindowBallPlay(double dt,double width,double height)
    {
        if(!StandingOnWindow||ball==null)return;
        double min=previousPerch.Left+width*.4,max=previousPerch.Right-width*.4;
        if(min>=max){velocityX=0;return;}
        double desired=x+width/2+velocityX*dt,next=Math.Clamp(desired,min,max);
        if(!NativeDesktop.IsExposed(new NativeDesktop.Surface(perch,previousPerch),next)){velocityX=0;return;}
        if(desired!=next){velocityX=0;if(ballPlayKind==4)kickDirection=next==min?1:-1;}
        x=next-width/2;y=previousPerch.Top-GroundAnchor(height);perchFraction=(next-previousPerch.Left)/previousPerch.Width;
    }
    private bool TryThrowOntoWindow(ToyBallWindow toy,double width,double height,NativeDesktop.Rect area)
    {
        foreach(var candidate in surfaces.OrderBy(s=>Math.Abs(s.Bounds.Left+s.Bounds.Width/2.0-toy.CenterX)))
        {
            if(candidate.Handle==perch||!NativeDesktop.TrySurface(candidate.Handle,out var r)||NativeDesktop.IsZoomed(candidate.Handle))continue;
            double targetX=Math.Clamp(toy.CenterX,r.Left+width*.6,Math.Max(r.Left+width*.6,r.Right-width*.6));
            var targetArea=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)targetX,Y=r.Top+8});
            if(!ScreenAllowed(targetArea)||targetArea.Left!=area.Left||targetArea.Top!=area.Top||Math.Abs(targetX-toy.CenterX)>width*3.5||
                !ExplorationRules.CanReach(y+GroundAnchor(height),r.Top,GroundAnchor(height),area.Top,area.Height,r.Width,width))continue;
            var fresh=new NativeDesktop.Surface(candidate.Handle,r);
            if(!NativeDesktop.IsExposed(fresh,targetX)||!NativeDesktop.IsExposed(fresh,targetX-width*.2)||!NativeDesktop.IsExposed(fresh,targetX+width*.2))continue;
            if(toy.TossToWindow(fresh,targetX,area)){autonomous="把球拋上窗沿";return true;}
        }
        return false;
    }
}
