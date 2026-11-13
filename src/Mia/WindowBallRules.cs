using System;

namespace Mia;

public static class WindowBallRules
{
    public static bool CrossesTop(double oldBottom,double bottom,double speed,double top)=>speed>0&&oldBottom<=top+1&&bottom>=top;
    public static double ContactX(double oldX,double x,double oldBottom,double bottom,double top)=>oldX+(x-oldX)*Math.Clamp((top-oldBottom)/Math.Max(.001,bottom-oldBottom),0,1);
    public static bool Settles(double sample)=>sample<.65;
    public static (double X,double Y)? ThrowToTop(double x,double y,double targetX,double top,double radius,double screenTop,double gravity,double scale)
    {
        double peak=top-radius-Math.Min(80*scale,Math.Max(0,(top-radius-screenTop-radius-12*scale)*.5));
        if(peak<screenTop+radius+12*scale||peak>=y||gravity<=0)return null;
        double up=Math.Sqrt(2*gravity*(y-peak));
        double time=(up+Math.Sqrt(Math.Max(0,up*up+2*gravity*(top-radius-y))))/gravity;
        double vx=(targetX-x)/time;
        return Math.Abs(vx)>600*scale?null:(vx,-up);
    }
    public static double RollSpeed(double velocity,double dt,double scale)
    {
        // Keep a slow roll so a ball on a wide ledge eventually reaches an edge.
        double sign=velocity<0?-1:1;
        return sign*Math.Max(18*scale,Math.Abs(velocity)*Math.Exp(-.3*dt));
    }
}
