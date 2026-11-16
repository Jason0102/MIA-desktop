using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mia.Services;
using Forms = System.Windows.Forms;

namespace Mia;

public sealed partial class App
{
    private ScheduledNotices? _scheduledNotices;
    private DispatcherTimer? _scheduledNoticeTimer;
    private bool _scheduledBalloon;
    private readonly CancellationTokenSource _scheduledSpeech = new();
    private Window? _scheduledWindow;
    private ComboBox? _scheduledList;
    private TextBox? _scheduledText;

    private void InitializeScheduledNotices(Forms.ContextMenuStrip menu)
    {
        if (Hermes is null || Environment.GetEnvironmentVariable("MIA_CRON_DELIVERY_ENABLED") != "true" ||
            Environment.GetEnvironmentVariable("MIA_CODEX_WORKSPACE") is not { Length: > 0 } workspace ||
            !Path.IsPathFullyQualified(workspace)) return;
        _scheduledNotices = new(workspace);
        menu.Items.Insert(3, new Forms.ToolStripMenuItem("排程結果與提醒", null, (_, _) => Dispatcher.Invoke(ShowScheduledNotices)));
        _scheduledNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _scheduledNoticeTimer.Tick += (_, _) => ReceiveScheduledNotices();
        _scheduledNoticeTimer.Start();
    }

    private void ReceiveScheduledNotices()
    {
        if (_exiting || _scheduledNotices is null || _tray is null) return;
        try
        {
            var notices = _scheduledNotices.Receive();
            if (notices.Count == 0) return;
            RefreshScheduledNotices();
            _scheduledBalloon = true;
            var first = notices[0];
            string summary = first.Text.Length > 155 ? first.Text[..155] + "…" : first.Text;
            string title = first.Kind == "reminder" ? "米亞 · 定時提醒" : "米亞 · 排程結果";
            _tray.ShowBalloonTip(5000, title, notices.Count == 1
                ? $"{first.Title}\n{summary}" : $"收到 {notices.Count} 則排程結果與提醒。點此查看。",
                Forms.ToolTipIcon.Info);
            if (!Settings.Muted && !string.IsNullOrWhiteSpace(Settings.VoiceId))
            {
                _chat ??= new ChatWindow(this);
                string speech = notices.Count == 1 ? $"{first.Title}。{summary}" : $"收到 {notices.Count} 則排程結果與提醒。";
                _ = _chat.SpeakScheduledNoticeAsync(speech, _scheduledSpeech.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void ShowScheduledNotices()
    {
        if (_exiting || _scheduledNotices is null) return;
        if (_scheduledWindow is null)
        {
            var layout = new DockPanel { Margin = new Thickness(14) };
            _scheduledList = new ComboBox { DisplayMemberPath = nameof(ScheduledNotice.Label), Margin = new Thickness(0, 0, 0, 10) };
            _scheduledText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _scheduledList.SelectionChanged += (_, _) => _scheduledText.Text =
                (_scheduledList.SelectedItem as ScheduledNotice)?.Text ?? "目前沒有排程通知。";
            DockPanel.SetDock(_scheduledList, Dock.Top);
            layout.Children.Add(_scheduledList);
            layout.Children.Add(_scheduledText);
            _scheduledWindow = new Window { Title = "米亞 · 排程結果與提醒", Width = 560, Height = 420,
                MinWidth = 320, MinHeight = 240, Content = layout };
            _scheduledWindow.Closed += (_, _) => { _scheduledWindow = null; _scheduledList = null; _scheduledText = null; };
        }
        RefreshScheduledNotices();
        _scheduledWindow.Show();
        _scheduledWindow.Activate();
    }

    private void RefreshScheduledNotices()
    {
        if (_scheduledList is null || _scheduledText is null || _scheduledNotices is null) return;
        string? selected = (_scheduledList.SelectedItem as ScheduledNotice)?.Id;
        try
        {
            var notices = _scheduledNotices.History();
            _scheduledList.ItemsSource = notices;
            _scheduledList.SelectedItem = notices.FirstOrDefault(notice => notice.Id == selected) ?? notices.FirstOrDefault();
            if (notices.Count == 0) _scheduledText.Text = "目前沒有排程通知。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scheduledText.Text = "無法讀取排程結果，請檢查米亞工作區。";
        }
    }

    private void StopScheduledNotices()
    {
        _scheduledNoticeTimer?.Stop();
        _scheduledSpeech.Cancel();
        _scheduledSpeech.Dispose();
        _scheduledWindow?.Close();
    }
}
