namespace Mia;

public sealed partial class ChatWindow
{
    internal async Task SpeakScheduledNoticeAsync(string text, CancellationToken ct)
    {
        using var speech = CancellationTokenSource.CreateLinkedTokenSource(ct, _speechInterruption.Token);
        try
        {
            await _speechQueue.WaitAsync(speech.Token);
            try { await _app.Voice.SpeakAsync(text, speech.Token); }
            finally { _speechQueue.Release(); }
        }
        catch (OperationCanceledException) when (speech.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
