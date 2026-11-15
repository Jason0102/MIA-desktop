using System.Windows;
using System.Windows.Controls;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    private sealed class PendingQuestion(WorkItem job, HermesControl control)
    {
        internal string QuestionId { get; } = Guid.NewGuid().ToString("N");
        internal WorkItem Job { get; } = job;
        internal HermesControl Control { get; } = control;
        internal StackPanel Actions { get; } = new();
        internal bool Sending { get; set; }
    }
    private readonly Dictionary<string, PendingQuestion> _pendingQuestions = new();
    private string? _answerTarget;

    private void AddJobControl(WorkItem job, HermesControl control)
    {
        if (job.Controls.Keys.Any(c => c.RunId == control.RunId && c.Kind == control.Kind &&
            c.RequestId == control.RequestId && c.Generation == control.Generation)) return;
        var question = new PendingQuestion(job, control);
        job.Controls.Add(control, question.Actions);
        _pendingQuestions.Add(question.QuestionId, question);
        job.WaitingForAnswer = true;
        job.Status.Text = "待確認";
        RememberTask(job, "待確認");
        _app.ShowChat();
        question.Actions.Children.Add(Ui.Button("回覆這項詢問", () =>
        {
            if (!_pendingQuestions.ContainsKey(question.QuestionId)) return;
            _answerTarget = question.QuestionId;
            _status.Text = "正在回覆「" + job.Title + "」；輸入後送出";
            _input.Focus();
        }));
        question.Actions.Children.Add(Ui.Button("取消指定", () => { _answerTarget = null; RefreshJobNotice(); }));
        if (control.Kind == "approval.request")
        {
            question.Actions.Children.Add(Ui.Button("允許這一次", () => Submit("允許這一次")));
            question.Actions.Children.Add(Ui.Button("拒絕", () => Submit("拒絕")));
        }
        RefreshJobNotice();
        _ = AnnounceAsync();

        void Submit(string answer)
        {
            AddMessage("你 · " + job.Title, answer, false);
            _ = AnswerQuestionAsync(question.QuestionId, answer, false);
        }
        async Task AnnounceAsync()
        {
            string text = "[audio: curious]「" + job.Title + "」需要你確認：\n" + control.Text +
                (control.Kind == "approval.request" ? "\n請回覆「允許這一次」或「拒絕」。" : "\n請直接在對話回答；多項詢問時可先點選「回覆這項詢問」。");
            try
            {
                var presentation = new ReplyPresentation(this, "米亞 · " + job.Title + " · 額外詢問", job.Cancellation.Token);
                presentation.AttachActions(question.Actions);
                await presentation.CompleteAsync(text);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!_app.IsExiting) AddMessage("米亞 · " + job.Title, text, true); }
        }
    }

    private async Task AnswerQuestionAsync(string questionId, string answer, bool hasImages)
    {
        if (!_pendingQuestions.TryGetValue(questionId, out var question) || question.Sending)
        { AddMessage("米亞", "這項詢問已送出或已結束，請確認目前的待確認項目。", true); return; }
        if (hasImages)
        { AddMessage("米亞", "這項確認目前只能接收文字，請用文字回答或另開附圖任務。", true); return; }
        bool approval = question.Control.Kind == "approval.request";
        string? response = approval ? ApprovalAnswer.Parse(answer, question.Job.Title) : answer.Trim();
        if (string.IsNullOrWhiteSpace(response))
        { AddMessage("米亞 · " + question.Job.Title, "尚未核准。請明確回覆「允許這一次」或「拒絕」，也可以點選按鈕。", true); return; }
        question.Sending = true;
        question.Actions.IsEnabled = false;
        try
        {
            await _app.Hermes!.RespondAsync(question.Control, response, question.Job.Cancellation.Token);
            CloseQuestion(question, approval && response == "deny" ? "已拒絕" : "已回覆");
            if (_jobs.Contains(question.Job) && question.Job.Reply is null && !question.Job.Cancellation.IsCancellationRequested)
                RememberTask(question.Job, question.Job.WaitingForAnswer ? "待確認" : "執行中");
            await PresentReplyAsync("米亞 · " + question.Job.Title,
                approval && response == "deny" ? "[audio: peaceful]已送出拒絕，米亞會依結果處理後續工作。" :
                "[audio: peaceful]已把你的回答送回這項任務。", _routingCancellation.Token);
        }
        catch (Exception ex)
        {
            if (_pendingQuestions.ContainsKey(questionId))
            {
                question.Sending = false;
                question.Actions.IsEnabled = true;
                question.Job.Status.Text = "待確認";
                if (!_app.IsExiting) AddMessage("米亞 · " + question.Job.Title, "尚未確認送出成功。" + Friendly(ex), true);
            }
        }
    }

    private void CloseQuestion(PendingQuestion question, string status)
    {
        _pendingQuestions.Remove(question.QuestionId);
        if (_answerTarget == question.QuestionId) _answerTarget = null;
        question.Actions.Children.Clear();
        question.Actions.Children.Add(Ui.Text(status, 12, Ui.Subtle));
        question.Actions.IsEnabled = false;
        question.Job.WaitingForAnswer = _pendingQuestions.Values.Any(q => q.Job == question.Job);
        question.Job.Status.Text = question.Job.WaitingForAnswer ? "待確認" : "正在繼續處理";
        RefreshJobNotice();
    }

    private void RefreshJobNotice()
    {
        int count = _pendingQuestions.Count;
        _status.Text = count > 0 ? $"{count} 項待確認 · 請在對話回答" : _jobs.Count > 0 ? $"正在處理 {_jobs.Count} 項任務" : "";
    }

    internal async Task<Dictionary<string, bool>> VerifyQuestionsAsync()
    {
        _app.Settings.Muted = true;
        var job = new WorkItem("詢問測試", "測試");
        _jobs.Add(job);
        var control = new HermesControl("v1/runs/test", "clarify.request", "question-one", 1, "請選擇輸出格式。");
        AddJobControl(job, control);
        AddJobControl(job, control);
        await Task.Delay(100);
        var checks = new Dictionary<string, bool>
        {
            ["duplicate_request_not_repeated"] = _pendingQuestions.Count == 1,
            ["work_center_waiting"] = job.WaitingForAnswer && job.Status.Text == "待確認",
            ["question_visible_in_chat"] = _messages.Children.OfType<System.Windows.Controls.Border>().Any(b =>
                b.Child is StackPanel p && p.Children.OfType<TextBox>().Any(t => t.Text.Contains("請選擇輸出格式")))
        };
        var first = _pendingQuestions.Values.Single();
        ((Button)first.Actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["reply_button_selects_exact_question"] = _answerTarget == first.QuestionId;
        var secondControl = new HermesControl("v1/runs/test", "approval.request", "question-two", 1, "是否允許這項操作？");
        AddJobControl(job, secondControl);
        await UpdateJobAsync(job, new HermesUpdate("message.delta", "progress"));
        checks["progress_preserves_waiting"] = job.Status.Text == "待確認";
        CloseQuestion(first, "已回覆");
        checks["other_question_remains_waiting"] = job.WaitingForAnswer && _pendingQuestions.Count == 1;
        checks["answered_selection_cleared"] = _answerTarget is null;
        var second = _pendingQuestions.Values.Single();
        checks["approval_buttons_in_chat"] = second.Actions.Children.OfType<Button>().Count() == 4;
        job.Cancellation.Cancel();
        CloseQuestion(second, "詢問已結束");
        checks["ended_questions_cannot_receive_answers"] = _pendingQuestions.Count == 0 && !second.Actions.IsEnabled;
        _jobs.Remove(job);
        await Task.Delay(100);
        job.Cancellation.Dispose();
        return checks;
    }
}
