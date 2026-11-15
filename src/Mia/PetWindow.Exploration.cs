using System;
using System.Windows;

namespace Mia;

public sealed partial class PetWindow
{
    private static readonly string[] idleActions={"ear-groom","collar-tidy","hand-warm","yawn","proud-nod"};
    private static readonly string[] idleNames={"整理耳邊頭髮","整理領口","輕搓雙手","掩嘴打哈欠","抱臂輕點頭"};
    private string idleAction="";
    private readonly CursorAttention cursorAttention=new();
    private int previousIdle=-1;
    private DateTime idleStarted,idleUntil,nextIdle=DateTime.UtcNow.AddSeconds(10);
    private bool perchSeated=true,autonomousJump;
    private DateTime seatUntil,platformDecisionAt,nextWindowJump;
    private IntPtr jumpTarget;
    private double PerchAnchor(double height)=>perchSeated && !speaking && !WorkingInCompanion?SeatAnchor(height):GroundAnchor(height);
    // Focus in the open banner permits stationary company, while movement and timed activities stay blocked.
    private bool FreeForCompanionIdle(DateTime now)=>!Paused&&!speaking&&!WorkingInCompanion&&(!listening||InteractionOpen)&&!dragging&&mood=="idle"&&now>=interactionUntil&&(now-pettedAt).TotalSeconds>1.5;
    private bool FreeForAutonomy(DateTime now)=>!InteractionOpen&&!listening&&FreeForCompanionIdle(now);

    private void UpdateCursorAttention(DateTime now,NativeDesktop.Point cursor,double width,double height)
    {
        bool wasActive=cursorAttention.Active;
        bool eligible=mode=="explore"&&ball==null&&grounded&&(perch==IntPtr.Zero||!perchSeated)&&FreeForAutonomy(now);
        if(cursorAttention.Update(now,eligible,CursorAttention.Distance(cursor.X,cursor.Y,x,y,width,height),random.NextDouble))
        {
            if(!wasActive)ClearIdleAction(now);
            velocityX=0;autonomous="停下看著游標";
        }
        else if(wasActive){actionUntil=DateTime.MinValue;autonomous="繼續探索";}
    }

    private void ClearIdleAction(DateTime now)
    {idleAction="";idleUntil=DateTime.MinValue;nextIdle=now.AddSeconds(8+random.Next(11));}
    private void StartIdleAction(DateTime now)
    {
        previousIdle=ExplorationRules.IdleChoice(previousIdle,random.Next(5));
        idleAction=idleActions[previousIdle];idleStarted=now;idleUntil=now.AddSeconds(3.2+random.NextDouble()*1.8);
        nextIdle=idleUntil.AddSeconds(8+random.Next(11));velocityX=0;autonomous=idleNames[previousIdle];
    }
    private void UpdateCompanionIdle(DateTime now)
    {
        if(mode!="companion"||(ball!=null&&!InteractionOpen)||!grounded||perch!=IntPtr.Zero||!FreeForCompanionIdle(now)||now<stretchUntil||timedCompanion.Current!=null)return;
        if(now>=nextIdle)StartIdleAction(now);
        if(now>=idleUntil)idleAction="";
    }
    private void ChooseExplorationMotion(DateTime now,double width,bool allowPause)
    {
        if(allowPause&&random.NextDouble()<.28)
        {StartIdleAction(now);actionUntil=idleUntil.AddSeconds(.5);return;}
        idleAction="";
        bool run=random.NextDouble()<.32;
        velocityX=(random.Next(2)==0?-1:1)*(run?165+random.Next(51):55+random.Next(60))*width/192;
        actionUntil=now.AddSeconds(ExplorationRules.MotionSeconds(random.NextDouble()));
        autonomous=run?"探索跑步":"探索散步";
    }
    private bool TryBeginWindowJump(DateTime now,double width,double height,NativeDesktop.Rect area)
    {
        if(mode!="explore"||cursorAttention.Active||ball!=null||!grounded||perch!=IntPtr.Zero||!FreeForAutonomy(now)||now<nextWindowJump||now<noDockUntil||now<idleUntil||Math.Abs(velocityX)<1)return false;
        double center=x+width/2,feet=y+GroundAnchor(height);
        foreach(var candidate in surfaces)
        {
            if(!NativeDesktop.TrySurface(candidate.Handle,out var r)||NativeDesktop.IsZoomed(candidate.Handle))continue;
            bool nearLeft=Math.Abs(center-r.Left)<width*.65,nearRight=Math.Abs(center-r.Right)<width*.65;
            if(!nearLeft&&!nearRight)continue;
            double landing=nearLeft?r.Left+width*.55:r.Right-width*.55;
            var targetArea=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)landing,Y=r.Top+8});
            if(!ScreenAllowed(targetArea)||targetArea.Left!=area.Left||targetArea.Top!=area.Top||!ExplorationRules.CanReach(feet,r.Top,GroundAnchor(height),area.Top,area.Height,r.Width,width))continue;
            var fresh=new NativeDesktop.Surface(candidate.Handle,r);
            if(!NativeDesktop.IsExposed(fresh,landing)||!NativeDesktop.IsExposed(fresh,landing-width*.2)||!NativeDesktop.IsExposed(fresh,landing+width*.2))continue;
            nextWindowJump=now.AddSeconds(10+random.Next(9));
            if(random.NextDouble()>.7)return false;
            LaunchWindowJump(now,fresh,landing,width,height,area);
            return true;
        }
        return false;
    }
    private void LaunchWindowJump(DateTime now,NativeDesktop.Surface target,double landing,double width,double height,NativeDesktop.Rect area)
    {
        var jump=ExplorationRules.Jump(y+GroundAnchor(height)-target.Bounds.Top,target.Bounds.Top-GroundAnchor(height)-area.Top,height);
        jumpTarget=target.Handle;autonomousJump=true;grounded=false;perch=IntPtr.Zero;velocityY=jump.VerticalSpeed;
        velocityX=(landing-(x+width/2))/jump.FlightSeconds;idleAction="";idleUntil=DateTime.MinValue;noDockUntil=DateTime.MinValue;
        nextWindowJump=now.AddSeconds(12);autonomous="跳上視窗";
    }
    private void SitOnPlatform(DateTime now,double height,bool byUser)
    {
        perchSeated=true;velocityX=0;seatUntil=now.AddSeconds(ExplorationRules.SeatSeconds(byUser,random.NextDouble()));
        y=previousPerch.Top-SeatAnchor(height);autonomous="窗沿坐下";idleAction="";
    }
    private void LeavePlatform(DateTime now,double width)
    {
        perch=IntPtr.Zero;perchSeated=true;jumpTarget=IntPtr.Zero;autonomousJump=false;
        grounded=false;velocityY=-170;velocityX=(random.Next(2)==0?-1:1)*95*width/192;
        noDockUntil=now.AddSeconds(2);nextWindowJump=now.AddSeconds(12);idleAction="";autonomous="跳下視窗";
    }
    private void UpdatePlatformExploration(DateTime now,double dt,double width,double height,NativeDesktop.Rect area)
    {
        if(perch==IntPtr.Zero)return;
        if(ball!=null){PrepareWindowBallPlay(now,width,height,area);return;}
        if(mode!="explore")
        {if(!perchSeated)SitOnPlatform(now,height,true);velocityX=0;return;}
        if(!FreeForAutonomy(now)||ball!=null||cursorAttention.Active){velocityX=0;return;}
        if(perchSeated)
        {
            if(now<seatUntil)return;
            if(previousPerch.Top-GroundAnchor(height)<area.Top+12||random.NextDouble()<.3){LeavePlatform(now,width);return;}
            perchSeated=false;y=previousPerch.Top-GroundAnchor(height);platformDecisionAt=now.AddSeconds(8+random.Next(9));
            ChooseExplorationMotion(now,width,false);
        }
        if(now>=platformDecisionAt)
        {
            double decision=random.NextDouble();platformDecisionAt=now.AddSeconds(8+random.Next(9));
            if(decision<.3){SitOnPlatform(now,height,false);return;}
            if(decision<.5){LeavePlatform(now,width);return;}
        }
        if(Math.Abs(velocityX)<1||now>=actionUntil)ChooseExplorationMotion(now,width,false);
        double min=previousPerch.Left+width*.4,max=previousPerch.Right-width*.4;
        if(min>=max){SitOnPlatform(now,height,false);return;}
        double next=x+width/2+velocityX*dt;
        bool edge=next<min||next>max;
        next=Math.Clamp(next,min,max);
        if(!NativeDesktop.IsExposed(new NativeDesktop.Surface(perch,previousPerch),next))
        {velocityX=-velocityX;autonomous="避開被遮住的窗沿";return;}
        x=next-width/2;perchFraction=(next-previousPerch.Left)/previousPerch.Width;
        y=previousPerch.Top-GroundAnchor(height);autonomous=Math.Abs(velocityX)*192/width>150?"窗沿跑步":"窗沿散步";
        if(edge)
        {
            double choice=random.NextDouble();
            if(choice<.45)LeavePlatform(now,width);
            else if(choice<.7)SitOnPlatform(now,height,false);
            else velocityX=-velocityX;
        }
    }
}
