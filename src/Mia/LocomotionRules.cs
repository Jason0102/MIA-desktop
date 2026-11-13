using System;

namespace Mia;

public static class LocomotionRules
{
    public const double WalkCycleDistance=140;
    public static bool IsWalk(string pose)=>pose is "walk-right" or "walk-left";
    public static string Pose(double physicalSpeed,double physicalWidth,string previousPose)
    {
        double speed=Math.Abs(physicalSpeed)*192/Math.Max(1,physicalWidth);
        if(speed<=3)return "idle";
        // Small hysteresis avoids switching poses repeatedly near the walk/run boundary.
        bool walking=speed<=(IsWalk(previousPose)?150:135);
        return walking?(physicalSpeed>0?"walk-right":"walk-left"):(physicalSpeed>0?"right":"left");
    }
    public static int WalkFrame(double logicalDistance,int count)
    {
        if(count<1)throw new ArgumentOutOfRangeException(nameof(count));
        return (int)Math.Floor(Math.Max(0,logicalDistance)%WalkCycleDistance/WalkCycleDistance*count);
    }
}
