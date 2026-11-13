using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Mia;

internal sealed class DockHintWindow : Window
{
    private readonly TextBlock label;
    public DockHintWindow()
    {
        Width=230;Height=30;WindowStyle=WindowStyle.None;AllowsTransparency=true;
        Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        label=new TextBlock { Foreground=Ui.Gold,FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(5,3,5,3) };
        Content=new Border { Background=new SolidColorBrush(Color.FromArgb(175,22,30,34)),BorderBrush=Ui.Gold,BorderThickness=new Thickness(0,0,0,2),CornerRadius=new CornerRadius(5,5,0,0),Child=label };
        SourceInitialized+=(_,_)=>NativeDesktop.ConfigureHint(new WindowInteropHelper(this).Handle);
    }
    public void Place(double center,double edge,string text)
    {
        label.Text=text;if(!IsVisible)Show();
        var h=new WindowInteropHelper(this).Handle;NativeDesktop.GetWindowRect(h,out var r);
        var area=NativeDesktop.WorkArea(new NativeDesktop.Point {X=(int)center,Y=(int)edge});
        var point=InteractionGeometry.ClampToWorkArea(center-r.Width/2.0,edge-r.Height,r.Width,r.Height,area.Left,area.Top,area.Right,area.Bottom);
        NativeDesktop.Position(h,point.X,point.Y);
    }
}
