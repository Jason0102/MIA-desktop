using Mia;

internal static class AttentionChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var state=new CursorAttention();var now=new DateTime(2026,1,1);
        int rolls=0;double Hit(){rolls++;return .1;}
        check(state.Update(now,true,.5,Hit),"nearby cursor can attract attention");
        check(state.Update(now.AddMinutes(2),true,.6,Hit)&&rolls==1,"attention holds until cursor leaves without repeated random trials");
        check(state.Update(now.AddMinutes(2),true,1.1,Hit),"attention boundary has hysteresis against jitter");
        check(!state.Update(now.AddMinutes(2),true,1.3,Hit)&&!state.Active,"leaving cursor releases attention immediately");
        check(!state.Update(now.AddMinutes(2).AddSeconds(1),true,.5,Hit)&&rolls==1,"rapid reentry during cooldown cannot interrupt again");
        state.Update(now.AddMinutes(3),true,2,Hit);
        check(state.Update(now.AddMinutes(3),true,.5,Hit)&&rolls==2,"fresh approach after cooldown can attract attention again");
        check(!state.Update(now.AddMinutes(3),false,.5,Hit)&&!state.Active,"dragging dialogue ball play and other ineligible states cancel attention");
        state=new CursorAttention();rolls=0;
        check(!state.Update(now,true,.5,()=>{rolls++;return .9;}),"approach can be ignored probabilistically");
        for(int i=0;i<300;i++)state.Update(now.AddSeconds(i),true,.5,Hit);
        check(!state.Active&&rolls==1,"ignored approach does not retry every frame even after cooldown");
        check(Math.Abs(CursorAttention.Distance(-904,-906.4,-1000,-1000,192,208))<.001,"attention supports negative monitor coordinates");
        check(Math.Abs(CursorAttention.Distance(300,100,0,0,192,208)-CursorAttention.Distance(900,300,0,0,576,624))<.001,"attention proximity scales with character and display DPI");
        int Cycles(bool slower)
        {
            int cycles=0,previous=0;
            for(int i=0;i<12000;i++)
            {int frame=slower?InteractionGeometry.IdleBlinkFrame(i*.001,6,8):InteractionGeometry.FrameIndex(i*.001,6,8);if(frame==1&&previous==0)cycles++;previous=frame;}
            return cycles;
        }
        check(Cycles(true)*2==Cycles(false),"idle blink cycles occur at exactly half the original frequency");
        check(Enumerable.Range(0,6).All(i=>InteractionGeometry.IdleBlinkFrame(.75+(i+.5)/8,6,8)==i),"blink closing and opening frames retain original speed");
        check(InteractionGeometry.IdleBlinkFrame(.6,6,8)==0,"extra idle rest holds the open-eye frame");
    }
}
