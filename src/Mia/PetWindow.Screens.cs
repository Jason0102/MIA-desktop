using System;
using System.Collections.Generic;
using System.Linq;

namespace Mia;

public sealed partial class PetWindow
{
    private ScreenRegion[] screens=Array.Empty<ScreenRegion>();
    private string[]? allowedScreenIds;
    private DateTime nextScreenRefresh;
    public IReadOnlyList<ScreenRegion> DetectedScreens
    {get{RefreshScreens();return screens;}}
    public IReadOnlyList<ScreenRegion> AllowedScreens=>ScreenRules.Allowed(DetectedScreens,allowedScreenIds);
    private void RefreshScreens()
    {
        if(DateTime.UtcNow<nextScreenRefresh)return;
        nextScreenRefresh=DateTime.UtcNow.AddSeconds(2);
        var detected=NativeDesktop.Screens();if(detected.Count>0)screens=detected.ToArray();
    }
    public void SetAllowedScreens(string[]? selected)
    {
        allowedScreenIds=selected?.ToArray();nextScreenRefresh=DateTime.MinValue;
        if(initialized)EnforceScreenBounds();
    }
    internal NativeDesktop.Rect AllowedWorkArea(NativeDesktop.Point point)
    {
        var available=AllowedScreens;
        return available.Count==0?NativeDesktop.WorkArea(point):ScreenRules.Nearest(available,point.X,point.Y).Work;
    }
    private bool ScreenAllowed(NativeDesktop.Rect area)=>AllowedScreens.Any(s=>s.Left==area.Left&&s.Top==area.Top);
    private void EnforceScreenBounds()
    {
        if(hwnd==IntPtr.Zero||!NativeDesktop.GetWindowRect(hwnd,out var b))return;
        var center=new NativeDesktop.Point {X=b.Left+b.Width/2,Y=b.Top+b.Height/2};
        var actual=NativeDesktop.WorkArea(center);var area=AllowedWorkArea(center);
        if(ScreenAllowed(actual)&&center.X>=actual.Left&&center.X<actual.Right&&center.Y>=actual.Top&&center.Y<actual.Bottom)return;
        MarkInteraction();perch=IntPtr.Zero;autonomousJump=false;jumpTarget=IntPtr.Zero;
        grounded=true;velocityX=velocityY=0;noDockUntil=DateTime.UtcNow.AddSeconds(2);
        if(dragging){dragging=false;dragMoved=false;ReleaseMouseCapture();}
        x=Math.Clamp(b.Left,area.Left,Math.Max(area.Left,area.Right-b.Width));y=area.Bottom-GroundAnchor(b.Height);
        NativeDesktop.Position(hwnd,x,y);dockHint?.Hide();
    }
    private void ClampDragToAllowedScreen()
    {
        NativeDesktop.GetWindowRect(hwnd,out var b);
        var area=AllowedWorkArea(new NativeDesktop.Point {X=(int)(x+b.Width/2),Y=(int)(y+b.Height/2)});
        (x,y)=InteractionGeometry.ClampToWorkArea(x,y,b.Width,GroundAnchor(b.Height),area.Left,area.Top,area.Right,area.Bottom);
    }
}
