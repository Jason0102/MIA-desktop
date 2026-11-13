using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mia;

internal static class Ui
{
    internal static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(233,232,222));
    internal static readonly Brush Subtle = new SolidColorBrush(Color.FromRgb(165,177,179));
    internal static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(238,195,100));
    internal static readonly Brush Panel = new SolidColorBrush(Color.FromArgb(110,33,42,47));
    internal static void Style(Window window)
    {
        window.Background = new SolidColorBrush(Color.FromRgb(19,27,32));
        window.Foreground = Ink;
        window.FontFamily = new FontFamily("Microsoft JhengHei UI");
        window.FontSize = 14;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
    internal static TextBlock Text(string text, double size=14, Brush? brush=null) => new TextBlock {
        Text=text, FontSize=size, Foreground=brush??Ink, TextWrapping=TextWrapping.Wrap,
        Margin=new Thickness(0,0,0,10) };
    internal static Button Button(string text, System.Action action, bool primary=false)
    {
        var b = new Button { Content=text, Padding=new Thickness(13,8,13,8),
            Margin=new Thickness(0,4,8,4), Background=primary?Gold:Panel,
            Foreground=primary?Brushes.Black:Ink, BorderThickness=new Thickness(0), Cursor=System.Windows.Input.Cursors.Hand };
        b.Click += (_,_)=>action(); return b;
    }
    internal static TextBox Input(string value="") => new TextBox { Text=value, Background=Panel,
        Foreground=Ink, CaretBrush=Gold, BorderBrush=Subtle, Padding=new Thickness(9), Margin=new Thickness(0,0,0,12) };
}
