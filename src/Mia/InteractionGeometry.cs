namespace Mia;

/// <summary>Physical-pixel motion rules, separated from native window discovery.</summary>
public static class InteractionGeometry
{
    public static bool MustLeavePerch(double surfaceTop,double anchorHeight,double screenTop,bool maximized) =>
        maximized || surfaceTop-anchorHeight<=screenTop;
    public static bool CanStretch(double idleSeconds,bool grounded,bool docked,bool paused,string mode,
        bool interactionOpen,bool speaking,bool listening,bool moodIdle,bool hasAsset) =>
        double.IsFinite(idleSeconds) && idleSeconds>=90 && grounded && !docked && !paused && mode!="focus" &&
        !interactionOpen && !speaking && !listening && moodIdle && hasAsset;
    public static bool HasDragStarted(double dx,double dy,double dpiScale=1) =>
        Math.Abs(dx)+Math.Abs(dy)>=6*Math.Max(.5,dpiScale);
    public static bool CanSnap(double centerX,double contactY,double surfaceLeft,double surfaceRight,
        double surfaceTop,double workTop,double anchorHeight,double tolerance) =>
        surfaceRight>surfaceLeft && centerX>surfaceLeft+12 && centerX<surfaceRight-12 &&
        surfaceTop-anchorHeight>=workTop && Math.Abs(contactY-surfaceTop)<=Math.Max(0,tolerance);
    public static (double X,double Y) ClampToWorkArea(double x,double y,double width,double height,
        double left,double top,double right,double bottom) =>
        (Math.Clamp(x,left,Math.Max(left,right-width)),Math.Clamp(y,top,Math.Max(top,bottom-height)));
    public static int FrameIndex(double seconds,int count,double fps)
    {
        if(count<1 || !double.IsFinite(fps) || fps<=0)throw new ArgumentOutOfRangeException(nameof(count));
        return (int)(Math.Floor(Math.Max(0,seconds)*fps)%count);
    }
    public static int IdleBlinkFrame(double seconds,int count,double fps)
    {
        // Double the open-eye rest, preserving the original closing/opening speed.
        double cycle=count/fps,phase=Math.Max(0,seconds)%(cycle*2);
        return phase<cycle?0:FrameIndex(phase-cycle,count,fps);
    }
}
