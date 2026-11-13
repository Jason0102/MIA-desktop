using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Mia;

public sealed partial class SettingsWindow
{
    private readonly StackPanel _screenChoices=new(){Margin=new Thickness(0,2,0,14)};
    private string _screenSignature="";
    private bool _screenUpdating;
    private void RefreshScreenChoices()
    {
        var screens=_app.Pet.DetectedScreens;
        string signature=string.Join("|",screens)+"/"+string.Join("|",_app.Settings.AllowedScreenIds??Array.Empty<string>());
        if(signature==_screenSignature)return;_screenSignature=signature;
        _screenUpdating=true;_screenChoices.Children.Clear();
        _screenChoices.Children.Add(Ui.Text($"活動螢幕 · 偵測到 {screens.Count} 個螢幕"));
        var all=new CheckBox {Content="所有螢幕（包含新接入的螢幕）",Tag="all-screens",Foreground=Ui.Ink,IsChecked=_app.Settings.AllowedScreenIds==null||_app.Settings.AllowedScreenIds.Length==0,Margin=new Thickness(0,5,0,8)};
        _screenChoices.Children.Add(all);
        all.Checked+=(_,_)=>{if(_screenUpdating)return;_app.Settings.AllowedScreenIds=null;_app.ApplySettings();RefreshScreenChoices();};
        all.Unchecked+=(_,_)=>{if(_screenUpdating)return;_app.Settings.AllowedScreenIds=_app.Pet.AllowedScreens.Select(s=>s.Id).ToArray();_app.ApplySettings();RefreshScreenChoices();};
        var allowed=_app.Pet.AllowedScreens.Select(s=>s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var screen in screens)
        {
            var box=new CheckBox {Content=screen.Label,Tag=screen.Id,Foreground=Ui.Ink,IsChecked=allowed.Contains(screen.Id),Margin=new Thickness(12,4,0,6)};
            _screenChoices.Children.Add(box);
            void Changed()
            {
                if(_screenUpdating)return;
                var selected=_screenChoices.Children.OfType<CheckBox>().Where(c=>c!=all&&c.IsChecked==true).Select(c=>(string)c.Tag).ToArray();
                if(selected.Length==0){_screenUpdating=true;box.IsChecked=true;_screenUpdating=false;_status.Text="請至少保留一個活動螢幕。";return;}
                _app.Settings.AllowedScreenIds=selected;_app.ApplySettings();_status.Text="活動螢幕已自動套用。";RefreshScreenChoices();
            }
            box.Checked+=(_,_)=>Changed();box.Unchecked+=(_,_)=>Changed();
        }
        bool missing=_app.Settings.AllowedScreenIds is {Length:>0} ids&&!screens.Any(s=>ids.Contains(s.Id,StringComparer.OrdinalIgnoreCase));
        _screenChoices.Children.Add(Ui.Text(missing?"指定螢幕未連線，暫用主螢幕；重新接入後恢復。":"至少勾選一個。人物與球只在勾選範圍活動；接入／拔除會自動更新。",12,Ui.Subtle));
        _screenUpdating=false;
    }
}
