using System;
using System.Linq;

namespace Mia;

// Wall time selects the local period; UTC deadlines keep duration independent of timezone offsets.
public sealed class TimedCompanion
{
    public sealed record Activity(string Id,string Label,int Period);
    public static readonly Activity[] Activities = {
        new("night-sleep","躺平睡覺",0), new("night-doze","縮腳抱膝打盹",0),
        new("morning-coffee","喝咖啡",1), new("morning-read","看書",1),
        new("afternoon-tea","喝手搖飲料",2), new("afternoon-draw","畫插畫",2),
        new("evening-squid","吃魷魚串",3), new("evening-music","戴有線耳塞聽音樂",3)
    };
    public static int Period(DateTime local) => local.Hour < 8 ? 0 : local.Hour < 12 ? 1 : local.Hour < 18 ? 2 : 3;
    public static bool IsActivity(string id) => Activities.Any(a=>a.Id==id);
    public Activity? Current { get; private set; }
    public DateTime Started { get; private set; }
    public DateTime Until { get; private set; }
    public DateTime NextAttempt { get; private set; }
    private string previous="";
    public const double StartDelaySeconds=20;
    public const double TriggerProbability=.70;
    public TimedCompanion(DateTime now) { NextAttempt=now.AddSeconds(StartDelaySeconds); }
    public void Cancel(DateTime now)
    {
        Current=null; Until=DateTime.MinValue;
        NextAttempt=now.AddSeconds(StartDelaySeconds);
    }
    public void Update(DateTime now,DateTime local,bool eligible,bool canStart,Func<double> random,Func<string,bool> available)
    {
        if(Current is { } current)
        {
            if(!eligible || current.Period!=Period(local) || now>=Until || now<Started)
            { Cancel(now); NextAttempt=now.AddSeconds(30+random()*20); }
            return;
        }
        if(!eligible) { NextAttempt=now.AddSeconds(StartDelaySeconds); return; }
        if(!canStart || now<NextAttempt)return;
        NextAttempt=now.AddSeconds(15+random()*10);
        if(random()>=TriggerProbability)return;
        var choices=Activities.Where(a=>a.Period==Period(local)&&available(a.Id)).ToArray();
        if(choices.Length==0)return;
        var fresh=choices.Where(a=>a.Id!=previous).ToArray();
        if(fresh.Length>0)choices=fresh;
        Current=choices[Math.Clamp((int)(random()*choices.Length),0,choices.Length-1)];
        previous=Current.Id; Started=now; Until=now.AddSeconds(35+random()*55);
    }
    // Long holds and short action beats avoid a mechanical two-frame flicker.
    public static int Frame(string id,double seconds,int count)
    {
        if(count<=1)return 0;
        double cycle=id switch {"night-sleep"=>6,"night-doze"=>8,"morning-read"=>10,"afternoon-draw"=>2.4,"evening-music"=>5,_=>7};
        double phase=Math.Max(0,seconds)%cycle/cycle;
        return phase >= .62 && phase < .88 ? 1 : 0;
    }
}
