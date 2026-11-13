namespace Mia;

// Deliberate oscillation, not speed alone: three large reversals in 1.5 seconds.
internal sealed class WindowShakeDetector
{
    private bool initialized;
    private double pivotX,pivotY,extremeX,extremeY,lastTime,firstTurn;
    private int axis,direction,reversals;
    public void Reset(){initialized=false;direction=0;reversals=0;}
    public bool Sample(double x,double y,double seconds)
    {
        if(!initialized || (direction!=0 && seconds-lastTime>.5))
        {initialized=true;pivotX=extremeX=x;pivotY=extremeY=y;lastTime=seconds;direction=0;reversals=0;return false;}
        double dx=x-pivotX,dy=y-pivotY;
        if(direction==0)
        {
            if(Math.Max(Math.Abs(dx),Math.Abs(dy))<80)return false;
            axis=Math.Abs(dx)>=Math.Abs(dy)?0:1;direction=Math.Sign(axis==0?dx:dy);
            extremeX=x;extremeY=y;lastTime=seconds;return false;
        }
        double retreat=(axis==0?x-extremeX:y-extremeY)*direction;
        if(retreat>0){extremeX=x;extremeY=y;return false;}
        if(retreat>-80)return false;
        if(seconds-lastTime>.4){Reset();return false;}
        if(reversals==0 || seconds-firstTurn>1.5){firstTurn=seconds;reversals=0;}
        reversals++;direction=-direction;extremeX=x;extremeY=y;lastTime=seconds;
        if(reversals<3)return false;
        Reset();return true;
    }
}
