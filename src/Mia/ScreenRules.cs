using System;
using System.Collections.Generic;
using System.Linq;

namespace Mia;

public sealed record ScreenRegion(string Id,int Left,int Top,int Right,int Bottom,bool Primary,int PixelWidth,int PixelHeight)
{
    public string Label=>$"螢幕 {Id.Replace(@"\\.\DISPLAY","")} · {PixelWidth} × {PixelHeight}"+(Primary?" · 主螢幕":"")+(PixelHeight>PixelWidth?" · 直立":"");
    internal NativeDesktop.Rect Work=>new(){Left=Left,Top=Top,Right=Right,Bottom=Bottom};
}
public static class ScreenRules
{
    public static ScreenRegion[] Allowed(IReadOnlyList<ScreenRegion> screens,string[]? selected)
    {
        if(selected==null||selected.Length==0)return screens.ToArray();
        var result=screens.Where(s=>selected.Contains(s.Id,StringComparer.OrdinalIgnoreCase)).ToArray();
        // Preserve saved preferences while temporarily using the primary display if all selected displays are absent.
        return result.Length>0?result:screens.OrderByDescending(s=>s.Primary).Take(1).ToArray();
    }
    public static ScreenRegion Nearest(IReadOnlyList<ScreenRegion> screens,double x,double y)=>screens.OrderBy(s=>
    {
        double dx=x-Math.Clamp(x,s.Left,s.Right-1),dy=y-Math.Clamp(y,s.Top,s.Bottom-1);
        return dx*dx+dy*dy;
    }).First();
}
