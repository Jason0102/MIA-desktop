using Mia;

internal static class ScreenChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var left=new ScreenRegion("left",-1080,-500,0,1380,false,1080,1920);
        var main=new ScreenRegion("main",0,0,1920,1040,true,1920,1080);
        var right=new ScreenRegion("right",1920,0,4480,1400,false,2560,1440);
        var screens=new[]{left,main,right};
        check(ScreenRules.Allowed(screens,null).Length==3,"default screen scope includes every connected display");
        check(ScreenRules.Allowed(screens,new[]{"left","right"}).SequenceEqual(new[]{left,right}),"explicit scope excludes unselected middle display");
        check(ScreenRules.Nearest(new[]{left,right},600,100)==left,"nearest allowed screen uses negative portrait coordinates");
        check(ScreenRules.Nearest(new[]{left,right},1850,100)==right,"relocation selects the nearest permitted display");
        check(ScreenRules.Allowed(new[]{main},new[]{"left"}).Single()==main,"unplugging all selected screens temporarily falls back to primary");
        check(ScreenRules.Allowed(screens,new[]{"left"}).Single()==left,"reconnecting restores original selected screen");
        check(ScreenRules.Allowed(new[]{left,right},new[]{"missing"}).Length==1,"fallback still works if no display is marked primary");
        check(ScreenRules.Allowed(screens,new[]{"MAIN","main"}).Single()==main,"screen identifiers match case-insensitively without duplicates");
        check(ScreenRules.Allowed(screens.Reverse().ToArray(),new[]{"left"}).Single()==left,"selection survives display enumeration order changes");
        check(ScreenRules.Allowed(screens,new[]{"main"}).Length==1,"newly connected displays stay excluded from an explicit scope");
        check(left.Label.Contains("直立")&&main.Label.Contains("主螢幕"),"screen labels identify portrait orientation and primary display");
    }
}
