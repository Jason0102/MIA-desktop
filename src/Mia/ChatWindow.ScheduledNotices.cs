namespace Mia;

public sealed partial class ChatWindow
{
    internal async Task SpeakScheduledNoticeAsync(string text, CancellationToken ct)
    {
        try
        {
            await _speechQueue.WaitAsync(ct);
            try { await _app.Voice.SpeakAsync(text, ct); }
            finally { _speechQueue.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
