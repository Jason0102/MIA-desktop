using System;

namespace Mia;

public static class BallPlayRules
{
    public static string NormalizeMode(string? mode)=>mode is "explore" or "play"?"explore":"companion";
    public static (double X,double Y) ThrowVelocity(double clearance,double gravity,double angle,double strength)
    {
        if(!double.IsFinite(clearance)||!double.IsFinite(gravity)||clearance<=0||gravity<=0)return (0,0);
        double rise=clearance*(.3+.55*Math.Clamp(strength,0,1));
        double up=Math.Sqrt(2*gravity*rise);
        return (Math.Tan(Math.Clamp(angle,-.85,.85))*up,-up);
    }
    public static bool CanHead(double previousBottom,double bottom,double headY,double dx,double width,double verticalSpeed,bool held) =>
        !held && verticalSpeed>0 && Math.Abs(dx)<=width*.23 && previousBottom<=headY && bottom>=headY;
    public static bool ShouldMove(string mode,bool ballPresent,bool interacting)=>!interacting && (ballPresent||NormalizeMode(mode)=="explore");
    public static double PredictHeadX(double x,double y,double vx,double vy,double gravity,double headY,double radius,double left,double right,double terminalSpeed=double.PositiveInfinity)
    {
        double discriminant=vy*vy+2*gravity*(headY-radius-y);
        if(discriminant<0 || gravity<=0 || right-left<=2*radius)return x;
        double time=(-vy+Math.Sqrt(discriminant))/gravity;
        if(double.IsFinite(terminalSpeed) && terminalSpeed>0)
        {
            double cappedAt=Math.Max(0,(terminalSpeed-vy)/gravity);
            if(time>cappedAt)
            {
                double distanceBeforeCap=vy*cappedAt+.5*gravity*cappedAt*cappedAt;
                time=cappedAt+(headY-radius-y-distanceBeforeCap)/terminalSpeed;
            }
        }
        if(time<0 || time>4)return x;
        double span=right-left-2*radius,travel=x+vx*time-left-radius;
        double wrapped=((travel%(2*span))+2*span)%(2*span);
        return left+radius+(wrapped>span?2*span-wrapped:wrapped);
    }
}
