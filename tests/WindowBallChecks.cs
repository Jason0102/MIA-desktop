using Mia;

internal static class WindowBallChecks
{
    internal static void Run(Action<bool,string> check)
    {
        check(WindowBallRules.CrossesTop(450,530,650,500),"fast descending ball detects a swept window-top contact");
        check(!WindowBallRules.CrossesTop(530,450,-650,500),"ascending ball passes up through a window top");
        check(!WindowBallRules.CrossesTop(540,560,200,500),"ball already below a window cannot attach remotely");
        check(Math.Abs(WindowBallRules.ContactX(100,180,450,530,500)-150)<.001,"window contact uses interpolated horizontal position");
        check(WindowBallRules.Settles(.64)&&!WindowBallRules.Settles(.65),"window-top settle chance has a sixty-five percent boundary");
        check(WindowBallRules.RollSpeed(0,.033,1)>0&&WindowBallRules.RollSpeed(-.1,.033,1)<0,"window roll never stalls and preserves its direction");
        check(Math.Abs(WindowBallRules.RollSpeed(120,.1,3)-3*WindowBallRules.RollSpeed(40,.1,1))<.001,"rolling speed scales with character and display size");
        var launch=WindowBallRules.ThrowToTop(100,900,400,500,21,0,900,1);
        check(launch.HasValue&&launch.Value.X>0&&launch.Value.Y<0,"targeted window toss launches up toward the platform");
        if(launch is {} velocity)
        {
            double time=300/velocity.X;
            check(Math.Abs(900+velocity.Y*time+450*time*time-479)<.01,"targeted throw reaches the window top on its descending arc");
            check(velocity.Y+900*time>0,"targeted platform contact is descending");
            check(900-velocity.Y*velocity.Y/1800>21,"targeted throw preserves top-screen clearance");
        }
        check(WindowBallRules.ThrowToTop(0,900,9000,500,21,0,900,1)==null,"targeted throw rejects excessive lateral velocity");
        check(WindowBallRules.ThrowToTop(0,900,100,30,21,0,900,1)==null,"window toss rejects a platform without ball headroom");
        var negative=WindowBallRules.ThrowToTop(-900,-100,-600,-500,21,-1000,900,1);
        check(negative.HasValue&&Math.Abs(negative.Value.X-launch!.Value.X)<.001,"targeted throws support negative monitor coordinates");
    }
}
