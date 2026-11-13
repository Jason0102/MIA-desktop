namespace Mia;

public static class CompanionRules
{
    public static bool CanKick(double dx,double ballY,double floorY,double width,double height,bool held) =>
        !held && System.Math.Abs(dx)<=width*.52 && ballY>=floorY-height*.24 && ballY<=floorY;
    // Hysteresis prevents constant start/stop when the pointer rests near the pet.
    public static double FollowSpeed(double delta,double width,bool moving)
    {
        double stop=width*.65,start=width*.95;
        if(System.Math.Abs(delta)<(moving?stop:start))return 0;
        return System.Math.Sign(delta)*System.Math.Clamp(System.Math.Abs(delta)*1.8,90,300)*(width/192);
    }
}
public sealed class FocusClock
{
    public double RemainingSeconds { get; private set; }
    public bool Running { get; private set; }
    public bool Completed { get; private set; }
    public void Start(int minutes){RemainingSeconds=System.Math.Clamp(minutes,1,120)*60;Running=true;Completed=false;}
    public void Stop(){Running=false;Completed=false;RemainingSeconds=0;}
    public void Tick(double seconds,bool paused)
    {
        if(!Running||paused||!double.IsFinite(seconds)||seconds<0)return;
        RemainingSeconds=System.Math.Max(0,RemainingSeconds-seconds);
        if(RemainingSeconds==0){Running=false;Completed=true;}
    }
}
