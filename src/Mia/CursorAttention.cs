using System;

namespace Mia;

/// <summary>One chance per approach; hold attention until the pointer leaves.</summary>
public sealed class CursorAttention
{
    public bool Active { get; private set; }
    private bool nearby;
    private DateTime nextChance;

    public static double Distance(double cursorX,double cursorY,double x,double y,double width,double height)
    {
        double dx=(cursorX-x-width*.5)/(width*.9),dy=(cursorY-y-height*.45)/(height*.7);
        return Math.Sqrt(dx*dx+dy*dy);
    }
    public void Cancel(DateTime now)
    {Active=false;nearby=false;nextChance=now.AddSeconds(8);}
    public bool Update(DateTime now,bool eligible,double distance,Func<double> sample)
    {
        if(!eligible){if(Active)Cancel(now);return false;}
        // A slightly larger exit boundary prevents jitter at the approach boundary.
        if(distance>1.25)
        {if(Active)nextChance=now.AddSeconds(8);Active=false;nearby=false;return false;}
        if(!nearby&&distance<=1)
        {
            nearby=true;
            if(now>=nextChance){Active=sample()<.45;nextChance=now.AddSeconds(8);}
        }
        return Active;
    }
}
