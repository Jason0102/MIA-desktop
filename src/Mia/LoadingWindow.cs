using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mia;

internal sealed class LoadingWindow : Window
{
    internal LoadingWindow(string statePath, int launcherPid)
    {
        Title = "米亞正在啟動";
        Width = 380; Height = 175; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = true;
        FontFamily = new FontFamily("Microsoft JhengHei UI");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.Text("米亞正在準備中", 20, Ui.Gold));
        var label = Ui.Text("整理工作空間…", 13, Ui.Subtle);
        label.Margin = new Thickness(0, 12, 0, 12);
        body.Children.Add(label);
        var progress = new ProgressBar { Height = 5, Maximum = 100, Foreground = Ui.Gold,
            Background = Ui.Panel, BorderThickness = new Thickness(0) };
        body.Children.Add(progress);
        Content = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            BorderBrush = Ui.Subtle, Background = new SolidColorBrush(Color.FromRgb(19, 27, 32)), Child = body };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            try
            {
                using var launcher = Process.GetProcessById(launcherPid);
                if (launcher.HasExited || File.Exists(statePath + ".ready")) { Close(); return; }
                using var data = JsonDocument.Parse(File.ReadAllText(statePath));
                progress.Value = Math.Clamp(data.RootElement.GetProperty("percent").GetDouble(), 0, 100);
                label.Text = data.RootElement.GetProperty("message").GetString();
            }
            catch (ArgumentException) { Close(); }
            catch (IOException) { }
            catch (JsonException) { }
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
