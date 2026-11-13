using System;

namespace Mia;

public sealed partial class PetWindow
{
    private readonly TimedCompanion timedCompanion=new(DateTime.UtcNow);
    private void UpdateTimedCompanion(DateTime now)
    {
        bool eligible=mode=="companion"&&ball==null&&grounded&&perch==IntPtr.Zero&&FreeForAutonomy(now);
        bool wasActive=timedCompanion.Current!=null;
        timedCompanion.Update(now,now.ToLocalTime(),eligible,now>=idleUntil&&now>=stretchUntil,
            random.NextDouble,id=>animations.Get(id)!=null);
        if(timedCompanion.Current is { } activity)
        { velocityX=0; autonomous=activity.Label; }
        else if(wasActive)
        { ClearIdleAction(now); lastInteraction=now; autonomous="原地陪伴"; }
    }
}
