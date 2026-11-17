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
    private readonly TextBlock _kanbanStatus = Ui.Text("", 12, Ui.Subtle);
    private readonly SemaphoreSlim _kanbanGate = new(1);
    private Task _kanbanExecution = Task.CompletedTask;
    private string? _kanbanSnapshot;
    private bool _kanbanRecovered;
    private KanbanCard[] _boardCards = [];

    private void InitializeKanban()
    {
        if (_app.Hermes is null || Environment.GetCommandLineArgs().Any(arg => arg.Contains("self-test"))) return;
        _kanban = HermesKanban.FromEnvironment();
        if (_kanban is null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => _ = PollKanbanAsync();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private async Task PollKanbanAsync()
    {
        if (_kanban is not { } board || _app.IsExiting || _clearingConversation || !await _kanbanGate.WaitAsync(0)) return;
        try
        {
            if (!board.BoardExists)
            {
                RenderKanban(new KanbanState([], null, null, null));
                return;
            }
            if (!_kanbanRecovered)
            {
                var recovered = await board.CallAsync(new { action = "recover", owner = board.Owner });
                _kanbanRecovered = true;
                RenderKanban(recovered);
            }
            if (_app.IsExiting || _clearingConversation) return;

            string action = _kanbanExecution.IsCompleted ? "advance" : "list";
            var state = await board.CallAsync(new { action, owner = board.Owner });
            _kanbanStatus.Text = "";
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
        { _kanbanStatus.Text = ex is IOException ? ex.Message : "無法更新工作狀態。"; RenderActivity(); }
        finally { _kanbanGate.Release(); }
    }

    private static bool KanbanFailure(Exception ex) =>
        ex is IOException or JsonException or System.ComponentModel.Win32Exception;

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
        string snapshot = JsonSerializer.Serialize(state.Cards);
        if (snapshot == _kanbanSnapshot) return;
        _kanbanSnapshot = snapshot;
        _boardCards = state.Cards;
        RenderActivity();
    }

    private void RenderBoardStatus()
    {
        foreach (var card in _boardCards.Where(c => c.Status != "done" && !_jobs.Any(j => j.SessionId == c.Session && j.Reply is null)))
        {
            string status = card.Status switch {
                "todo" => "等待前置", "ready" => "排隊中", "running" => "執行中",
                "review" => "待審核", "blocked" => "已阻塞", _ => "需處理" };
            var details = new StackPanel();
            details.Children.Add(Ui.Text(card.Body, 13));
            if (card.Result.Length > 0) details.Children.Add(Ui.Text(SpeechTags.Display(card.Result), 13));
            if (card.Status == "review") details.Children.Add(Ui.Text("請在對話中說明通過或需要修改的內容。", 12, Ui.Subtle));
            _activity.Children.Add(new Expander { Header = status + " · " + card.Title + "（" + card.Id + "）", Content = details });
        }
    }
}
