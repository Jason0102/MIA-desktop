using System;
using System.Collections.Generic;

namespace Mia;

// Physical cursor samples, independent of mouse-event frequency and display DPI.
public sealed class BallThrowTracker
{
    private readonly Queue<(double X,double Y,double Time)> samples=new();
    public void Reset(double x,double y,double time){samples.Clear();Sample(x,y,time);}
    public void Sample(double x,double y,double time)
    {
        if(samples.Count>0 && time<=samples.Peek().Time)return;
        samples.Enqueue((x,y,time));
        while(samples.Count>2 && time-samples.Peek().Time>.12)samples.Dequeue();
    }
    public (double X,double Y) Release(double now,double scale)
    {
        if(samples.Count<2)return (0,0);
        var first=samples.Peek();var last=System.Linq.Enumerable.Last(samples);
        if(now-last.Time>.10 || last.Time-first.Time<.008)return (0,0);
        double dt=last.Time-first.Time;
        double x=(last.X-first.X)/dt,y=(last.Y-first.Y)/dt;
        double speed=Math.Sqrt(x*x+y*y),limit=1100*Math.Max(.5,scale);
        if(speed<25*scale)return (0,0);
        double factor=speed>limit?limit/speed:1;
        return (x*factor,y*factor);
    }
}
