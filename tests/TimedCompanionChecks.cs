using System.IO;
using System.Reflection;
using Mia;
using Mia.Services;

internal static class TimedCompanionChecks
{
    internal static void Run(Action<bool,string> check)
    {
        DateTime origin=new(2026,11,10,0,0,0,DateTimeKind.Utc);
        var frequent=new TimedCompanion(origin);
        check(frequent.NextAttempt==origin.AddSeconds(20),"timed activity initial delay reduced to twenty seconds");
        frequent.Update(origin.AddSeconds(20),origin,true,true,()=>.69,_=>true);
        check(frequent.Current!=null,"sixty-nine percent draw now triggers a timed activity");
        var finished=frequent.Until;
        frequent.Update(finished,origin,true,true,()=>.5,_=>true);
        check(frequent.NextAttempt==finished.AddSeconds(40),"completed activity leaves thirty to fifty seconds for ordinary gestures");
        var retryAt=frequent.NextAttempt;
        frequent.Update(retryAt,origin,true,true,()=>.70,_=>true);
        check(frequent.Current==null&&frequent.NextAttempt==retryAt.AddSeconds(22),"seventy percent cutoff misses and retries within fifteen to twenty-five seconds");
        foreach(var (hour,period) in new[]{(0,0),(7,0),(8,1),(11,1),(12,2),(17,2),(18,3),(23,3)})
            check(TimedCompanion.Period(origin.AddHours(hour))==period,$"local hour {hour} selects period {period}");
        foreach(int hour in new[]{7,11,17,23})
        {
            var s=new TimedCompanion(origin);DateTime now=origin.AddSeconds(61),local=origin.Date.AddHours(hour).AddMinutes(59);
            s.Update(now,local,true,true,()=>0,_=>true);
            check(s.Current?.Period==TimedCompanion.Period(local),"only current period starts");
            var first=s.Current!.Id;
            s.Update(now.AddSeconds(20),local,true,true,()=>0,_=>true);
            check(s.Current?.Id==first,"activity survives multiple frames for its duration");
            s.Update(now.AddSeconds(21),local.AddMinutes(1),true,true,()=>0,_=>true);
            check(s.Current==null&&s.NextAttempt>now.AddSeconds(21),"period boundary including midnight ends activity and imposes gap");
            s.Update(s.NextAttempt,local,true,true,()=>0,_=>true);
            check(s.Current!=null&&s.Current.Id!=first,"same period avoids immediate repetition");
            s.Update(s.Until,local,true,true,()=>0,_=>true);
            check(s.Current==null,"duration expiry returns to ordinary idle");
        }
        var clock=new TimedCompanion(origin);
        clock.Update(origin.AddSeconds(1),origin,true,true,()=>0,_=>true);
        check(clock.Current==null,"no activity immediately on launch");
        clock.Update(origin.AddSeconds(61),origin,true,true,()=>.9,_=>true);
        check(clock.Current==null,"random miss retains ordinary idle");
        clock.Update(clock.NextAttempt,origin,true,false,()=>0,_=>true);
        check(clock.Current==null,"short idle gestures and stretching finish before timed activity");
        clock.Update(clock.NextAttempt,origin,true,true,()=>0,_=>false);
        check(clock.Current==null,"missing art never starts invisible activity");
        clock.Update(clock.NextAttempt,origin,true,true,()=>0,_=>true);
        check(clock.Current!=null,"eligible activity starts after cooldown");
        clock.Update(clock.Started.AddSeconds(1),origin,false,true,()=>0,_=>true);
        check(clock.Current==null,"interaction ball pause exploration or airborne eligibility loss interrupts");
        foreach(var a in TimedCompanion.Activities)
        {
            check(Enumerable.Range(0,300).Select(i=>TimedCompanion.Frame(a.Id,i*.1,2)).Distinct().Count()==2,"each activity animates both frames");
        }
        var loaded=RoleProfile.Load(Path.Combine(AppContext.BaseDirectory,"role.json"));
        check(loaded.Warning==null&&loaded.Instructions.Contains("貓精靈桌面夥伴")&&loaded.Instructions.Contains("喜歡探索"),"bundled public persona loaded");
        string scratch=Path.Combine(AppContext.BaseDirectory,"test-data","role-invalid.json");
        File.WriteAllText(scratch,"{broken");
        check(RoleProfile.Load(scratch).Warning!=null,"invalid role falls back without crashing");
        File.WriteAllText(scratch,"{\"basic\":\"x\",\"mia\":{}}");
        check(RoleProfile.Load(scratch).Warning!=null,"missing identity falls back visibly");
        File.Delete(scratch);
        check(RoleProfile.Load(scratch).Warning!=null,"missing role falls back visibly");
        check(RoleProfile.TimeContext(new DateTimeOffset(2026,11,10,8,0,0,TimeSpan.FromHours(8)),"Taipei").Contains("2026-11-10 08:00:00 +08:00"),"current clock includes explicit local date time offset");
        TestPet(check,origin);
    }
    private static void TestPet(Action<bool,string> check,DateTime origin)
    {
        var pet=new PetWindow();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string field,object value)=>typeof(PetWindow).GetField(field,flags)!.SetValue(pet,value);
        void Call(string method,params object[] args)=>typeof(PetWindow).GetMethod(method,flags)!.Invoke(pet,args);
        var clock=(TimedCompanion)typeof(PetWindow).GetField("timedCompanion",flags)!.GetValue(pet)!;
        var native=typeof(PetWindow).Assembly.GetType("Mia.NativeDesktop+Point")!;
        var point=Activator.CreateInstance(native)!;
        try
        {
            Set("grounded",true);Set("mood","idle");
            foreach(var activity in TimedCompanion.Activities)
                check(pet.AvailableInteractionAnimations.Contains(activity.Id),"runtime loads "+activity.Id);
            var now=DateTime.UtcNow.AddMinutes(5);
            clock.Update(now,origin,true,true,()=>0,_=>true);
            Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation=="night-sleep","timed activity selected by actual pet pose pipeline");
            pet.SetSpeaking(true);
            Call("SelectPose",now,point,192d,208d);
            check(clock.Current==null&&pet.CurrentAnimation=="speaking","speech cancels timed pose immediately");
            pet.SetSpeaking(false);pet.Paused=true;
            clock.Update(now.AddMinutes(5),origin,true,true,()=>0,_=>true);
            Call("UpdateTimedCompanion",now.AddMinutes(5));
            check(clock.Current==null,"pause cancels actual pet timed activity");
            pet.Paused=false;
            clock.Update(now.AddMinutes(10),origin,true,true,()=>0,_=>true);
            check(clock.Current!=null,"fixture starts timed pose before opening banner");
            pet.InteractionOpen=true;pet.BeginListening(true);
            check(clock.Current==null,"opening banner immediately cancels timed activity even before next timer tick");
            now=DateTime.UtcNow.AddSeconds(2);
            Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation=="idle","focused open banner returns to ordinary blinking idle instead of fixed listening");
            Set("nextIdle",now.AddSeconds(-1));Call("UpdateCompanionIdle",now);
            Call("SelectPose",now,point,192d,208d);
            check(new[]{"ear-groom","collar-tidy","hand-warm","yawn","proud-nod"}.Contains(pet.CurrentAnimation),"focused banner allows ordinary stationary gestures");
            Call("UpdateTimedCompanion",now.AddMinutes(5));
            check(clock.Current==null,"long-open banner cannot start another timed activity");
            Set("idleUntil",DateTime.MinValue);Set("stretchUntil",now.AddSeconds(5));
            Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation=="stretching","open banner can display ordinary companion stretch");
            pet.SetMood("thinking");Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation=="thinking","thinking takes priority over banner idle gestures");
            pet.SetSpeaking(true);Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation=="speaking","speech takes priority over banner idle gestures");
            pet.SetSpeaking(false);pet.SetMood("idle");
            Set("nextIdle",now.AddSeconds(-1));Call("UpdateCompanionIdle",now);
            Call("SelectPose",now,point,192d,208d);
            check(pet.CurrentAnimation!="listening"&&!TimedCompanion.IsActivity(pet.CurrentAnimation),"finished speech returns to ordinary banner company");
            pet.InteractionOpen=false;pet.BeginListening(false);
            check(clock.Current==null&&clock.NextAttempt>DateTime.UtcNow.AddSeconds(18),"closing banner starts a fresh short cooldown without resuming interrupted activity");
            pet.SetMode("explore");pet.InteractionOpen=true;
            check(pet.EffectiveMode=="companion","banner temporarily uses stationary companion mode");
            pet.InteractionOpen=false;
            check(pet.EffectiveMode=="explore","closing banner restores configured exploration mode");
        }
        finally { pet.Stop();pet.Close(); }
    }
}
