using System;

namespace Mia;

public static class ExplorationRules
{
    public const double Gravity=1300;
    public const double MaxSeatSeconds=60;
    public static bool CanReach(double feet,double platformTop,double standingHeight,double screenTop,double screenHeight,double platformWidth,double petWidth)
    {
        double rise=feet-platformTop;
        return rise>24 && rise<=screenHeight*.78 && platformTop-standingHeight>=screenTop+12 && platformWidth>=petWidth*1.25;
    }
    public static (double VerticalSpeed,double FlightSeconds) Jump(double rise,double headroom,double height)
    {
        double peak=rise+Math.Min(height*.16,Math.Max(4,headroom-8));
        double speed=Math.Sqrt(2*Gravity*peak);
        return (-speed,(speed+Math.Sqrt(Math.Max(0,speed*speed-2*Gravity*rise)))/Gravity);
    }
    public static double MotionSeconds(double unit)=>8+Math.Clamp(unit,0,1)*8;
    public static double SeatSeconds(bool byUser,double unit)=>byUser?MaxSeatSeconds:20+Math.Clamp(unit,0,1)*30;
    public static int IdleChoice(int previous,int sample)=>previous<0?sample%5:(previous+1+sample%4)%5;
    // Play an anticipation/contact/recovery sequence once, rather than flickering between poses indefinitely.
    public static int IdleFrame(double seconds,double duration,int count)
    {
        if(count<=1)return 0;
        double t=Math.Clamp(seconds/Math.Max(.1,duration),0,1);
        return t<.22||t>.78?0:Math.Min(1,count-1);
    }
}
