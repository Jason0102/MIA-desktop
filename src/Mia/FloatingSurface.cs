using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mia;

internal static class FloatingSurface
{
    private static readonly Dictionary<Window,Action<double>> scalers=new();
    internal static void ApplyScale(double scale)
    {
        foreach(var apply in scalers.Values.ToArray())apply(Math.Clamp(scale,.5,3));
    }
    // Pure physical-pixel placement: no HWNDs, DPI virtualization or screen access.
    internal static Rect CalculatePlacement(Rect pet, Size panel, Rect workArea)
    {
        if(workArea.Width<=0 || workArea.Height<=0 || panel.Width<=0 || panel.Height<=0)
            throw new ArgumentOutOfRangeException(nameof(panel));
        double margin=Math.Min(8,Math.Min(workArea.Width,workArea.Height)/4);
        var usable=new Rect(workArea.Left+margin,workArea.Top+margin,
            workArea.Width-2*margin,workArea.Height-2*margin);
        double w=Math.Min(panel.Width,usable.Width),h=Math.Min(panel.Height,usable.Height);
        double x=pet.Left+(pet.Width-w)/2, y=pet.Top-h-10;
        if(y<usable.Top)
        {
            if(pet.Right+10+w<=usable.Right) { x=pet.Right+10;y=pet.Top; }
            else if(pet.Left-10-w>=usable.Left) { x=pet.Left-10-w;y=pet.Top; }
            else if(pet.Bottom+10+h<=usable.Bottom) { y=pet.Bottom+10; }
            else { y=usable.Top; }
        }
        return new Rect(Math.Clamp(x,usable.Left,usable.Right-w),
            Math.Clamp(y,usable.Top,usable.Bottom-h),w,h);
    }

    internal static void Attach(Window window, PetWindow pet, bool wrap=true, Func<bool>? suppressEscape=null)
    {
        window.WindowStyle=WindowStyle.None;
        window.ResizeMode=ResizeMode.NoResize;
        window.AllowsTransparency=true;
        window.Background=Brushes.Transparent;
        window.ShowInTaskbar=false;
        window.Topmost=true;
        window.Owner=pet;
        if(wrap)
        {
            var body=(UIElement)window.Content;
            window.Content=null;
            window.Content=new Border { Child=body, CornerRadius=new CornerRadius(18),
                Background=new SolidColorBrush(Color.FromArgb(195,22,30,34)),
                BorderBrush=new SolidColorBrush(Color.FromArgb(65,238,195,100)), BorderThickness=new Thickness(1) };
        }
        double baseWidth=window.Width,baseHeight=window.Height,baseMinWidth=window.MinWidth,baseMinHeight=window.MinHeight;
        double preferredWidth=baseWidth, minimumWidth=baseMinWidth, minimumHeight=baseMinHeight;
        void Scale(double factor)
        {
            preferredWidth=baseWidth*factor;minimumWidth=baseMinWidth*factor;minimumHeight=baseMinHeight*factor;
            if(window.Content is FrameworkElement body)body.LayoutTransform=new ScaleTransform(factor,factor);
            window.MinWidth=Math.Min(minimumWidth,window.MaxWidth);
            window.MinHeight=Math.Min(minimumHeight,window.MaxHeight);
            window.Width=Math.Min(preferredWidth,window.MaxWidth);
            if(double.IsFinite(baseHeight))window.Height=baseHeight*factor;
        }
        scalers[window]=Scale;
        Scale(((App)Application.Current).Settings.UiScale);
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
        void SynchronizeInteraction()
        {
            bool open=scalers.Keys.Any(w=>w.Owner==pet && w.IsVisible);
            pet.InteractionOpen=open;
        }
        void Position()
        {
            if(!window.IsVisible)return;
            var handle=new WindowInteropHelper(window).Handle;
            var petHandle=new WindowInteropHelper(pet).Handle;
            if(handle==IntPtr.Zero||petHandle==IntPtr.Zero)return;
            NativeDesktop.GetWindowRect(petHandle,out var p);
            NativeDesktop.GetWindowRect(handle,out var r);
            var area=NativeDesktop.WorkArea(new NativeDesktop.Point { X=p.Left+p.Width/2,Y=p.Top+p.Height/2 });
            if(area.Width<=0 || area.Height<=0 || r.Width<=0 || r.Height<=0)return;
            var dpi=VisualTreeHelper.GetDpi(window);
            double availableWidth=Math.Max(1,(area.Width-16)/dpi.DpiScaleX);
            double availableHeight=Math.Max(1,(area.Height-16)/dpi.DpiScaleY);
            // Restore preferred size when moving back to a larger monitor. Do not use
            // a 180-DIP minimum that can force the input capsule off a short display.
            window.MinWidth=Math.Min(minimumWidth,availableWidth);
            window.MinHeight=Math.Min(minimumHeight,availableHeight);
            window.MaxWidth=availableWidth;
            window.MaxHeight=availableHeight;
            window.Width=Math.Min(preferredWidth,availableWidth);
            var location=CalculatePlacement(new Rect(p.Left,p.Top,p.Width,p.Height),
                new Size(r.Width,r.Height),new Rect(area.Left,area.Top,area.Width,area.Height));
            if(Math.Abs(r.Left-location.Left)>=1 || Math.Abs(r.Top-location.Top)>=1)
                NativeDesktop.Position(handle,location.Left,location.Top);
        }
        timer.Tick+=(_,_)=>Position();
        window.IsVisibleChanged+=(_,_)=>
        {
            if(window.IsVisible){Position();timer.Start();}else timer.Stop();
            SynchronizeInteraction();
        };
        window.Closed+=(_,_)=>{scalers.Remove(window);timer.Stop();SynchronizeInteraction();};
        window.KeyDown+=(_,e)=>
        {
            // Let a text input/IME consume Escape before treating it as dismissal.
            if(e.Key==Key.Escape && !e.Handled && suppressEscape?.Invoke()!=true)
            {window.Hide();e.Handled=true;}
        };
        window.MouseEnter+=(_,_)=>window.Opacity=1;
        window.MouseLeave+=(_,_)=>window.Opacity=.9;
        window.Loaded+=(_,_)=>{Position();SynchronizeInteraction();};
    }
}
