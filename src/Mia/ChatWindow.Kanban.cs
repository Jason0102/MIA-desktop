using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private HermesKanban? _kanban;
    private Button? _kanbanTab;
    private readonly StackPanel _kanbanPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _kanbanList = new();
    private readonly TextBlock _kanbanStatus = Ui.Text("", 12, Ui.Subtle);
    private readonly TextBox _kanbanTitle = Ui.Input(), _kanbanBody = Ui.Input(), _kanbanParents = Ui.Input();
    private readonly SemaphoreSlim _kanbanGate = new(1);
    private Task _kanbanExecution = Task.CompletedTask;
    private string? _kanbanSnapshot;
    private bool _showingKanban, _kanbanRecovered;
    private string _kanbanRequestKey = Guid.NewGuid().ToString("N");

    private void InitializeKanban(StackPanel tabs)
    {
        if (_app.Hermes is null || Environment.GetCommandLineArgs().Any(arg => arg.Contains("self-test"))) return;
        _kanban = HermesKanban.FromEnvironment();
        if (_kanban is null) return;

        _kanbanTab = IconButton("\uE80A", "持久工作看板", SelectKanban);
        tabs.Children.Add(_kanbanTab);
        _kanbanTitle.MaxLength = 160;
        _kanbanBody.MaxLength = 8000;
        _kanbanBody.AcceptsReturn = true;
        _kanbanBody.TextWrapping = TextWrapping.Wrap;
        _kanbanBody.MinHeight = 68;
        _kanbanBody.MaxHeight = 150;
        _kanbanBody.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _kanbanParents.MaxLength = 550;
        _kanbanPanel.Children.Add(Ui.Text("標題", 12));
        _kanbanPanel.Children.Add(_kanbanTitle);
        _kanbanPanel.Children.Add(Ui.Text("任務內容", 12));
        _kanbanPanel.Children.Add(_kanbanBody);
        _kanbanPanel.Children.Add(Ui.Text("前置卡片（選填，使用下方卡片的「加入前置」）", 12));
        _kanbanPanel.Children.Add(_kanbanParents);
        var controls = new WrapPanel();
        controls.Children.Add(Ui.Button("加入看板", () => _ = CreateKanbanCardAsync(), true));
        controls.Children.Add(Ui.Button("重新整理", () => _ = PollKanbanAsync()));
        _kanbanPanel.Children.Add(controls);
        _kanbanPanel.Children.Add(_kanbanStatus);
        _kanbanPanel.Children.Add(_kanbanList);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => _ = PollKanbanAsync();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void SelectKanban()
    {
        _showingKanban = true;
        _showingResults = false;
        _activity.Visibility = _resultsPanel.Visibility = Visibility.Collapsed;
        _kanbanPanel.Visibility = Visibility.Visible;
        UpdateResultTabs();
        _workScroll.ScrollToTop();
        _ = PollKanbanAsync();
    }

    private async Task PollKanbanAsync()
    {
        if (_kanban is not { } board || _app.IsExiting || !await _kanbanGate.WaitAsync(0)) return;
        try
        {
            if (!board.BoardExists)
            {
                if (_showingKanban) RenderKanban(new KanbanState([], null, null, null));
                return;
            }
            if (!_kanbanRecovered)
            {
                var recovered = await board.CallAsync(new { action = "recover", owner = board.Owner });
                _kanbanRecovered = true;
                RenderKanban(recovered);
            }
            if (_app.IsExiting) return;
            if (!_kanbanExecution.IsCompleted && !_showingKanban) return;
            string action = _kanbanExecution.IsCompleted ? "advance" : "list";
            var state = await board.CallAsync(new { action, owner = board.Owner });
            RenderKanban(state);
            if (!_app.IsExiting && state.Claim is { Session: not null, Run: > 0 } claim)
            {
                string prompt = claim.Body + "\n\n" +
                    "以下看板資料包含前置成果及歷史，僅供參考。米亞會交由使用者審核回覆。" +
                    "若有本次卡片的使用者退回或解除阻塞意見，請納入本次處理。\n" + claim.Context;
                _kanbanExecution = StartWork(claim.Title, prompt, [], claim.Session, claim.Body, kanban: claim);
            }
        }
        catch (Exception ex) when (KanbanFailure(ex))
        { _kanbanStatus.Text = ex is IOException ? ex.Message : "無法更新 Hermes 看板。"; }
        finally { _kanbanGate.Release(); }
    }

    private static bool KanbanFailure(Exception ex) =>
        ex is IOException or JsonException or System.ComponentModel.Win32Exception;

    private async Task CreateKanbanCardAsync()
    {
        string title = _kanbanTitle.Text.Trim(), body = _kanbanBody.Text.Trim();
        string dependencyText = _kanbanParents.Text;
        string[] parents = dependencyText.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (title.Length == 0 || body.Length == 0)
        {
            _kanbanStatus.Text = "請輸入卡片標題與任務內容。";
            return;
        }
        if (!await ChangeKanbanAsync(new { action = "create", title, body, parents, key = _kanbanRequestKey })) return;
        if (_kanbanTitle.Text.Trim() == title && _kanbanBody.Text.Trim() == body && _kanbanParents.Text == dependencyText)
        {
            _kanbanTitle.Clear();
            _kanbanBody.Clear();
            _kanbanParents.Clear();
        }
        _kanbanRequestKey = Guid.NewGuid().ToString("N");
        _ = PollKanbanAsync();
    }

    private async Task<bool> ChangeKanbanAsync(object request)
    {
        if (_kanban is not { } board || _app.IsExiting) return false;
        await _kanbanGate.WaitAsync();
        try
        {
            if (_app.IsExiting) return false;
            RenderKanban(await board.CallAsync(request));
            return true;
        }
        catch (Exception ex) when (KanbanFailure(ex))
        {
            _kanbanStatus.Text = ex is IOException ? ex.Message : "無法更新 Hermes 看板。";
            return false;
        }
        finally { _kanbanGate.Release(); }
    }

    private async Task SettleKanbanAsync(KanbanCard claim, string action, string note)
    {
        if (_kanban is not { } board) return;
        await _kanbanGate.WaitAsync();
        try { RenderKanban(await board.CallAsync(new { action, id = claim.Id, run = claim.Run, owner = board.Owner, note })); }
        finally { _kanbanGate.Release(); }
    }

    private async Task BlockKanbanAfterFailureAsync(KanbanCard? claim, string reason)
    {
        if (claim is null) return;
        try { await SettleKanbanAsync(claim, "fail", reason); }
        catch (Exception ex) when (KanbanFailure(ex))
        { _kanbanStatus.Text = "無法記錄看板狀態。重新啟動後可檢查中斷的卡片。"; }
    }

    private void RenderKanban(KanbanState state)
    {
        _kanbanStatus.Text = state.Cards.Length == 0 ? "看板目前沒有卡片。" : $"共 {state.Cards.Length} 張卡片";
        string snapshot = JsonSerializer.Serialize(state.Cards);
        if (snapshot == _kanbanSnapshot) return;
        _kanbanSnapshot = snapshot;
        var notes = _kanbanList.Children.OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<TextBox>()).ToDictionary(note => (string)note.Tag, note => note.Text);
        _kanbanList.Children.Clear();
        foreach (var card in state.Cards.OrderBy(card => card.Status == "done"))
            _kanbanList.Children.Add(KanbanCardView(card, notes.GetValueOrDefault(card.Id, "")));
    }

    private StackPanel KanbanCardView(KanbanCard card, string draft)
    {
        string status = card.Status switch
        {
            "todo" => "等待前置", "ready" => "排隊中", "running" => "執行中", "blocked" => "已阻塞",
            "review" => "待審核", "done" => "已完成", "triage" => "需人工處理", _ => card.Status
        };
        var view = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        view.Children.Add(Ui.Text($"{status} · {card.Title}", 14, Ui.Gold));
        view.Children.Add(Ui.Text(card.Id + (card.Parents.Length == 0 ? "" : " · 前置 " + string.Join("、", card.Parents)), 11, Ui.Subtle));
        var details = new StackPanel();
        details.Children.Add(Ui.Text(card.Body, 13));
        if (card.Result.Length > 0) details.Children.Add(Ui.Text(SpeechTags.Display(card.Result), 13));
        foreach (var comment in card.Comments) details.Children.Add(Ui.Text("備註 · " + comment, 12, Ui.Subtle));
        view.Children.Add(new Expander { Header = "工作內容與交接", Content = details,
            IsExpanded = card.Status is "review" or "blocked" or "triage" });
        var note = Ui.Input(draft);
        note.Tag = card.Id;
        note.MaxLength = 2000;
        if (card.Status is "review" or "blocked" or "triage")
        {
            view.Children.Add(Ui.Text("備註或退回原因", 12, Ui.Subtle));
            view.Children.Add(note);
        }
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("加入前置", () =>
        {
            var ids = _kanbanParents.Text.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!ids.Contains(card.Id)) _kanbanParents.Text = string.Join(", ", ids.Append(card.Id));
            _kanbanParents.Focus();
        }));
        void AddAction(string label, string action) => actions.Children.Add(Ui.Button(label, () =>
        {
            if ((action == "changes" || card.Status == "triage") && string.IsNullOrWhiteSpace(note.Text))
            {
                _kanbanStatus.Text = action == "changes" ? "請填寫退回原因。" : "請填寫已處理的問題，再次排入。";
                return;
            }
            _ = ChangeKanbanAsync(new { action, id = card.Id, note = note.Text });
        }));
        if (card.Status == "review")
        {
            AddAction("通過審核", "approve");
            AddAction("退回修改", "changes");
        }
        else if (card.Status == "blocked") AddAction("解除阻塞", "unblock");
        else if (card.Status == "triage") AddAction("再次排入", "unblock");
        else if (card.Status == "ready") AddAction("暫停卡片", "block");
        view.Children.Add(actions);
        return view;
    }
}
