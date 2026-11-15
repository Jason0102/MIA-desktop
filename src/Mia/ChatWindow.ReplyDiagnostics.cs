using System.Net;
using System.Net.Http;
using Mia.Services;

namespace Mia;

public sealed partial class ChatWindow
{
    // Opt-in desktop check: synthetic PCM exercises the actual Windows playback/start callback.
    internal async Task<Dictionary<string, bool>> VerifyReplyPresentationAsync()
    {
        var checks = new Dictionary<string, bool>();
        bool muted = _app.Settings.Muted;
        string voiceId = _app.Settings.VoiceId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        try
        {
            _app.Settings.Muted = true;
            _app.Pet.SetMode("companion");
            _app.Pet.SetMood("idle");
            _app.Pet.SetTaskProcessing(true);
            await Task.Delay(100, ct);
            checks["working_uses_act008"] = _app.Pet.CurrentAnimation == "thinking";
            var direct = new ReplyPresentation(this, "米亞", ct);
            direct.Update("[audio: curious]你好");
            checks["muted_delta_displays_immediately"] = direct.VisibleText == "你好";
            await Task.Delay(100, ct);
            checks["muted_reply_overrides_task_animation"] = _app.Pet.CurrentAnimation == "speaking";
            direct.Update("[audio: curious]你好，我在這裡。");
            await direct.CompleteAsync("[audio: curious]你好，我在這裡。");
            await Task.Delay(100, ct);
            checks["muted_reply_restores_work_animation"] = _app.Pet.CurrentAnimation == "thinking";

            var longer = new ReplyPresentation(this, "米亞", ct);
            var textOnly = longer.CompleteAsync("[audio: curious]這段比較長的純文字回應，應該讓表情維持更久。");
            await Task.Delay(1500, ct);
            checks["longer_text_still_animates_after_old_timeout"] = !textOnly.IsCompleted && _app.Pet.CurrentAnimation == "speaking";
            await textOnly;

            _app.Settings.Muted = false;
            _app.Settings.VoiceId = "synthetic";
            int requests = 0;
            var allowAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new ReplyAudioHandler(async (_, token) =>
            {
                int requestNumber = ++requests;
                await allowAudio.Task.WaitAsync(token);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[requestNumber == 1 ? 96000 : 48000]) };
                response.Content.Headers.ContentType = new("audio/pcm");
                return response;
            });
            using var voice = new VoiceService(new AppSettings { ElevenLabsKey = "synthetic", VoiceId = "synthetic" }, handler);
            var emotions = new List<string>();
            voice.SpeakingChanged += _app.Pet.SetSpeaking;
            voice.SpeechAnimationChanged += animation => { emotions.Add(animation.Tag); _app.Pet.SetSpeechAnimation(animation); };
            var spoken = new ReplyPresentation(this, "米亞", ct, voice: voice);
            const string first = "[audio: happy]第一句開始播放時，文字應該逐步顯示。";
            const string final = first + "[audio: curious]接著是第二句。";
            spoken.Update(first);
            await Task.Delay(100, ct);
            checks["text_waits_for_audio_start"] = requests == 1 && spoken.VisibleText.Length == 0 && !voice.IsSpeaking;
            allowAudio.SetResult();
            bool streaming = false, speaking = false;
            while (spoken.VisibleText.Length == 0) await Task.Delay(10, ct);
            streaming = spoken.VisibleText.Length < SpeechTags.Display(first).Length;
            await Task.Delay(80, ct);
            speaking = _app.Pet.CurrentAnimation == "speaking" && voice.IsSpeaking;
            checks["audio_and_incremental_text_start_together"] = streaming && speaking;
            await Task.Delay(1250, ct);
            checks["speech_animation_remains_active_near_audio_end"] = voice.IsSpeaking && _app.Pet.CurrentAnimation == "speaking";
            while (voice.IsSpeaking) await Task.Delay(10, ct);
            await Task.Delay(100, ct);
            checks["speech_animation_releases_when_audio_drains"] = _app.Pet.CurrentAnimation == "thinking";
            spoken.Update(final);
            await spoken.CompleteAsync(final);
            await Task.Delay(100, ct);
            checks["sentence_emotions_reach_pet"] = emotions.Contains("happy") && emotions.Contains("curious");
            checks["full_reply_kept_without_tags_or_duplicates"] = spoken.VisibleText == SpeechTags.Display(final) && requests == 2;
            checks["audio_end_restores_act008"] = !voice.IsSpeaking && _app.Pet.CurrentAnimation == "thinking";

            _app.Settings.Muted = true;
            var disabled = new ReplyPresentation(this, "米亞", ct, voice: voice);
            await disabled.CompleteAsync("停用語音時直接顯示。");
            checks["disabled_voice_sends_no_audio_request"] = requests == 2 && disabled.VisibleText == "停用語音時直接顯示。";
            _app.Pet.SetTaskProcessing(false);
            await Task.Delay(100, ct);
            checks["last_task_end_releases_act008"] = _app.Pet.CurrentAnimation != "thinking";
        }
        finally
        {
            _app.Settings.Muted = muted;
            _app.Settings.VoiceId = voiceId;
            _app.Pet.SetTaskProcessing(false);
        }
        return checks;
    }

    private sealed class ReplyAudioHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
