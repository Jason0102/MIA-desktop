using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private DiscordWorkItem[] _discordWork = [];
    private string _discordWorkSnapshot = "";

    private void InitializeDiscordWork()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => RefreshDiscordWork();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { RefreshDiscordWork(); timer.Start(); }
            else timer.Stop();
        };
        Closed += (_, _) => timer.Stop();
        RefreshDiscordWork();
    }

    private void RefreshDiscordWork()
    {
        var items = DiscordWork.Read(SettingsStore.DataDirectory);
        string snapshot = JsonSerializer.Serialize(items);
        if (snapshot == _discordWorkSnapshot) return;
        _discordWork = items;
        _discordWorkSnapshot = snapshot;
        if (IsWorkCenter) { RenderActivity(); if (_showingResults) RenderResults(); }
    }

    private Border BuildDiscordCard(DiscordWorkItem item)
    {
        var content = new StackPanel();
        content.Children.Add(Ui.Text($"遠端訊息 · Discord · {item.StatusText} · {item.UpdatedAt.LocalDateTime:MM/dd HH:mm}", 12, Ui.Gold));
        var request = Ui.Text(item.Request, 14);
        request.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(request);
        if (item.Active) content.Children.Add(Ui.Text("可在 Discord 回覆原訊息補充要求，或傳 /stop 停止此工作。", 12, Ui.Subtle));
        if (item.Reply.Length > 0)
        {
            content.Children.Add(new Expander { Header = "查看回覆", Foreground = Ui.Subtle,
                Content = new TextBox { Text = SpeechTags.Display(item.Reply), IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap, Background = System.Windows.Media.Brushes.Transparent,
                    Foreground = Ui.Ink, BorderThickness = new Thickness(0) } });
        }
        foreach (string file in item.Files)
        {
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) continue;
            var button = Ui.Button(Path.GetFileName(file), () =>
            {
                try
                {
                    if (CanOpenResult(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                    else
                    {
                        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(file); Process.Start(start);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                { _resultStatus.Text = "無法開啟 Discord 成果檔案。"; }
            });
            button.ToolTip = "開啟 Discord 成果檔案";
            content.Children.Add(button);
        }
        return new Border { Child = content, Background = Ui.Panel, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
    }
}
