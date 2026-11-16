using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private readonly TextBox _resultSearch = Ui.Input();
    private readonly StackPanel _resultList = new();
    private readonly StackPanel _resultsPanel = new() { Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _workScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _workHeading = Ui.Text("進行中任務", 15);
    private readonly TextBlock _resultStatus = Ui.Text("", 12, Ui.Subtle);
    private readonly Dictionary<string, FrameworkElement> _resultCards = new();
    private TaskResults _taskResults = null!;
    private Button _activeTab = null!, _resultsTab = null!;
    private bool _showingResults;
    private int _resultLimit = 30;

    private void InitializeResults()
    {
        string workspace = Environment.GetEnvironmentVariable("MIA_CODEX_WORKSPACE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIA Workspace");
        _taskResults = new TaskResults(workspace, Path.Combine(SettingsStore.DataDirectory, "task-results.json"));
        try { _taskResults.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _resultStatus.Text = "無法讀取歷史成果索引。"; }

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _activeTab = IconButton("\uE8FD", "進行中任務", () => SelectResults(false));
        _resultsTab = IconButton("\uE8B7", "任務成果", () => SelectResults(true));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tabs, Dock.Right);
        tabs.Children.Add(_activeTab);
        tabs.Children.Add(_resultsTab);
        InitializeKanban();
        header.Children.Add(tabs);
        _workHeading.VerticalAlignment = VerticalAlignment.Center;
        _workHeading.Margin = new Thickness(0);
        header.Children.Add(_workHeading);
        DockPanel.SetDock(header, Dock.Top);
        _workPanel.Children.Add(header);

        var searchBox = new Grid();
        searchBox.Children.Add(_resultSearch);
        _resultSearch.MaxLength = 240;
        AutomationProperties.SetName(_resultSearch, "搜尋任務成果");
        var hint = Ui.Text("搜尋任務、檔名或日期…", 13, Ui.Subtle);
        hint.Margin = new Thickness(10, 8, 8, 0);
        hint.IsHitTestVisible = false;
        searchBox.Children.Add(hint);
        _resultSearch.TextChanged += (_, _) =>
        {
            hint.Visibility = _resultSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _resultLimit = 30;
            RenderResults();
        };
        _resultsPanel.Children.Add(searchBox);
        _resultsPanel.Children.Add(_resultStatus);
        _resultsPanel.Children.Add(_resultList);
        _workScroll.Content = new Grid { Children = { _activity, _resultsPanel } };
        _workPanel.Children.Add(_workScroll);
        UpdateResultTabs();
        RefreshResults(migrateLegacy: true);
    }

    private void RefreshResults(TaskResult? completed = null, bool migrateLegacy = false)
    {
        var history = _knownTasks.Where(task => task.State == "已完成" && task.Id != completed?.Id)
            .Select(task => new TaskResult(task.Id, task.Title, task.Request ?? task.Title, task.Result, task.UpdatedAt, []));
        if (completed is not null) history = history.Append(completed);
        try
        {
            _taskResults.Refresh(history, migrateLegacy ? new ConversationStore("hermes-conversation.jsonl").ReadTurns() : null);
            _taskResults.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _resultStatus.Text = "無法更新歷史成果索引。"; }
        if (_showingResults && IsWorkCenter) RenderResults();
    }

    internal void SelectResults(bool show, string? taskId = null)
    {
        _showingResults = show;
        _activity.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _resultsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateResultTabs();
        if (!show) { _workScroll.ScrollToTop(); RenderActivity(); return; }

        if (taskId is not null)
        {
            _resultSearch.Clear();
            _resultLimit = Math.Max(30, _taskResults.Entries.TakeWhile(result => result.Id != taskId).Count() + 1);
        }
        RefreshResults();
        _workScroll.ScrollToTop();
        if (taskId is not null && _resultCards.TryGetValue(taskId, out var card))
        {
            if (card is Border border)
            {
                border.BorderBrush = Ui.Gold;
                border.BorderThickness = new Thickness(1);
            }
            Dispatcher.BeginInvoke(new Action(() => card.BringIntoView()));
        }
    }

    private void UpdateResultTabs()
    {
        _workHeading.Text = _showingResults ? "任務成果" : "進行中任務";
        _activeTab.Foreground = _showingResults ? Ui.Subtle : Ui.Gold;
        _resultsTab.Foreground = _showingResults ? Ui.Gold : Ui.Subtle;
        AutomationProperties.SetItemStatus(_activeTab, _showingResults ? "" : "已選取");
        AutomationProperties.SetItemStatus(_resultsTab, _showingResults ? "已選取" : "");

    }

    private void RenderResults()
    {
        _resultCards.Clear();
        _resultList.Children.Clear();
        var matches = _taskResults.Search(_resultSearch.Text).Take(_resultLimit + 1).ToArray();
        if (matches.Length == 0)
            _resultList.Children.Add(Ui.Text(_resultSearch.Text.Length == 0 ? "目前沒有任務成果" : "找不到符合的成果", 13, Ui.Subtle));
        foreach (var result in matches.Take(_resultLimit))
        {
            var card = BuildResultCard(result, full: true);
            _resultList.Children.Add(card);
            _resultCards[result.Id] = card;
        }
        if (matches.Length > _resultLimit)
            _resultList.Children.Add(Ui.Button("載入更多", () => { _resultLimit += 30; RenderResults(); }));
    }

    private Border BuildResultCard(TaskResult result, bool full)
    {
        var content = new StackPanel();
        if (full)
        {
            var heading = new DockPanel();
            var rename = IconButton("\uE70F", "重新命名任務", () => EditResultTitle(result, content));
            DockPanel.SetDock(rename, Dock.Right);
            heading.Children.Add(rename);
            var title = new StackPanel();
            title.Children.Add(Ui.Text(result.Title, 14, Ui.Gold));
            if (result.UpdatedAt is { } date)
            {
                var local = date.LocalDateTime;
                string stamp = local.Date == DateTime.Today ? local.ToString("'今天' HH:mm")
                    : local.Date == DateTime.Today.AddDays(-1) ? local.ToString("'昨天' HH:mm") : local.ToString("yyyy/MM/dd HH:mm");
                title.Children.Add(Ui.Text(stamp, 12, Ui.Subtle));
            }
            heading.Children.Add(title);
            content.Children.Add(heading);
        }
        foreach (string file in result.Files)
        {
            string name = Path.GetFileName(file);
            var row = new DockPanel();
            var locate = IconButton("\uE8DA", "在資料夾中顯示", () => OpenResult(file, locate: true));
            DockPanel.SetDock(locate, Dock.Right);
            row.Children.Add(locate);
            bool canOpen = CanOpenResult(file);
            var open = Ui.Button(name, () => OpenResult(file, locate: !canOpen));
            open.Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Ink };
            open.HorizontalContentAlignment = HorizontalAlignment.Left;
            open.ToolTip = canOpen ? "開啟檔案" : "在資料夾中顯示";
            AutomationProperties.SetName(open, $"{open.ToolTip} {name}");
            row.Children.Add(open);
            if (_taskResults.Available(file) is not null) content.Children.Add(row);
            else content.Children.Add(Ui.Text($"{name} · 找不到檔案", 12, Ui.Subtle));
        }

        if (full)
        {
            foreach (string jobId in result.Files.Select(TaskResults.FileJobId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string folder = jobId + "/output";
                if (_taskResults.Available(folder, folder: true) is not null)
                    content.Children.Add(Ui.Button("開啟成果資料夾", () => OpenResult(folder, folder: true)));
            }
            if (result.Reply.Length > 0)
            {
                var reply = new Expander { Header = result.Files.Length == 0 ? "查看文字成果" : "查看完成回覆",
                    Foreground = Ui.Subtle, Margin = new Thickness(0, 5, 0, 0) };
                reply.Content = new TextBox
                {
                    Text = SpeechTags.Display(result.Reply), TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                    Background = System.Windows.Media.Brushes.Transparent, Foreground = Ui.Ink, BorderThickness = new Thickness(0)
                };
                content.Children.Add(reply);
            }
        }
        return new Border { Child = content, Background = Ui.Panel, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
    }

    private void EditResultTitle(TaskResult result, StackPanel card)
    {
        if (card.Children.OfType<TextBox>().Any()) return;
        var edit = Ui.Input(result.Title);
        edit.MaxLength = 120;
        AutomationProperties.SetName(edit, "任務名稱");
        void SaveTitle()
        {
            if (string.IsNullOrWhiteSpace(edit.Text)) { edit.Focus(); return; }
            _taskResults.Rename(result.Id, edit.Text.Trim());
            try { _taskResults.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _resultStatus.Text = "無法儲存任務名稱。"; }
            RenderResults();
        }
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("儲存", SaveTitle));
        buttons.Children.Add(Ui.Button("取消", () => { card.Children.Remove(edit); card.Children.Remove(buttons); }));
        card.Children.Insert(1, edit);
        card.Children.Insert(2, buttons);
        edit.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; SaveTitle(); } };
        edit.Focus();
        edit.SelectAll();
    }

    private static bool CanOpenResult(string file) => Path.GetExtension(file).ToLowerInvariant() is
        ".docx" or ".xlsx" or ".pptx" or ".pdf" or ".txt" or ".md" or ".csv" or ".tsv" or ".rtf"
        or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".mp3" or ".wav" or ".mp4" or ".zip";

    private void OpenResult(string relative, bool locate = false, bool folder = false)
    {
        _resultStatus.Text = "";
        try
        {
            string? path = _taskResults.Available(relative, folder);
            if (path is null) throw new FileNotFoundException();
            if (locate || (!folder && !CanOpenResult(path)))
            {
                var explorer = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                explorer.ArgumentList.Add("/select," + path);
                Process.Start(explorer);
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            string message = ex is FileNotFoundException ? "成果已移動或刪除。" : "無法開啟成果，請檢查檔案或應用程式。";
            if (IsWorkCenter) { _resultStatus.Text = message; RenderResults(); }
            else _status.Text = message;
        }
    }

    // Run with an isolated catalog containing one legacy job and one manifest-linked task.
    internal Dictionary<string, bool> VerifyResultsPanel()
    {
        _resultsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var entries = _taskResults.Entries.ToArray();
        var checks = new Dictionary<string, bool>
        {
            ["tab_switch"] = IsWorkCenter && _resultsPanel.Visibility == Visibility.Visible && _activity.Visibility == Visibility.Collapsed,
            ["banner_button_removed"] = !_bannerDetails.Children.OfType<DockPanel>().Any(),
            ["legacy_and_manifest_merged"] = entries.Length == 2 && entries[0].Title == "新版測試" && entries[1].Title == "舊版測試",
            ["job_paths_resolve"] = entries.SelectMany(entry => entry.Files).Count(file => _taskResults.Available(file) is not null) == 2,
            ["skips_incomplete_jobs"] = !entries.Any(entry => entry.Files.Any(file => file.Contains("未完成.txt"))),
            ["rejects_escape"] = _taskResults.Available(new string('a', 32) + "/output/../codex-result.txt") is null
        };
        _resultSearch.Text = "legacy.txt";
        checks["search_filters_by_filename"] = _resultCards.Count == 1 && _resultCards.Keys.Single() == entries.LastOrDefault()?.Id;
        _resultSearch.Text = "最新請求";
        checks["search_filters_by_request"] = _resultCards.Count == 1 && _resultCards.Keys.Single() == entries.FirstOrDefault()?.Id;
        if (entries.FirstOrDefault() is { } target)
        {
            var reply = AddMessage("米亞", target.Reply, true);
            if (reply.Parent is StackPanel panel) panel.Children.Add(BuildResultCard(target, full: false));
            var controls = reply.Parent is StackPanel replyPanel ? replyPanel.Children.OfType<Border>().Single().Child as StackPanel : null;
            checks["reply_card_has_file_and_locate_buttons"] = controls?.Children.OfType<DockPanel>().Any(row => row.Children.OfType<Button>().Count() == 2) == true;
            SelectResults(true, target.Id);
            checks["notification_selects_exact_card"] = _resultSearch.Text.Length == 0
                && _resultCards.TryGetValue(target.Id, out var card) && card is Border { BorderBrush: not null };
            _taskResults.Rename(target.Id, "重新命名後");
            _taskResults.Save();
            string workspace = Environment.GetEnvironmentVariable("MIA_CODEX_WORKSPACE")!;
            var reloaded = new TaskResults(workspace, Path.Combine(SettingsStore.DataDirectory, "task-results.json"));
            reloaded.Load();
            checks["rename_and_history_persist"] = reloaded.Find(target.Id)?.Title == "重新命名後" && reloaded.Entries.Count() == 2;
        }
        _activeTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["back_to_active"] = _activity.Visibility == Visibility.Visible && _resultsPanel.Visibility == Visibility.Collapsed;
        return checks;
    }
}
