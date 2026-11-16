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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

            var playbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int interruptedRequests = 0, playbackCount = 0;
            using var interruptHandler = new ReplyAudioHandler((_, _) =>
            {
                interruptedRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(new byte[128]) });
            });
            using var interruptVoice = new VoiceService(
                new AppSettings { ElevenLabsKey = "synthetic", VoiceId = "synthetic" }, interruptHandler,
                async (_, token) =>
                {
                    if (++playbackCount == 1)
                    {
                        playbackStarted.SetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                });
            var interruptedReply = new ReplyPresentation(this, "米亞", ct, voice: interruptVoice);
            const string interruptedText = "第一句正在播放。第二句不應再播放。";
            Task oldPlayback = interruptedReply.CompleteAsync(interruptedText);
            await playbackStarted.Task.WaitAsync(ct);
            var queuedReply = new ReplyPresentation(this, "米亞", ct, voice: interruptVoice);
            Task queuedPlayback = queuedReply.CompleteAsync("排隊的舊回覆。");
            InterruptReplyAudio();
            await Task.WhenAll(oldPlayback, queuedPlayback).WaitAsync(TimeSpan.FromSeconds(2), ct);
            checks["new_input_interrupts_audio_and_queued_sentences"] =
                interruptedRequests == 1 && playbackCount == 1;
            checks["interrupted_reply_keeps_full_text_and_task_token"] =
                interruptedReply.VisibleText == interruptedText && !ct.IsCancellationRequested;
            var replacement = new ReplyPresentation(this, "米亞", ct, voice: interruptVoice);
            await replacement.CompleteAsync("新的回覆。");
            checks["new_reply_audio_plays_after_interruption"] = playbackCount == 2;

            _app.Settings.VoiceId = "";
            using var localVoice = new VoiceService(new AppSettings(), handler);
            bool localStarted = false;
            localVoice.SpeakingChanged += speakingNow => localStarted |= speakingNow;
            var localReply = new ReplyPresentation(this, "米亞", ct, voice: localVoice);
            await localReply.CompleteAsync("米亞語音測試。");
            checks["missing_cloud_credentials_show_text_without_audio"] =
                !localStarted && !localVoice.IsSpeaking && localReply.VoiceError is not null &&
                localReply.VisibleText == "米亞語音測試。" && requests == 2;

            using var rejectedHandler = new ReplyAudioHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            using var rejectedVoice = new VoiceService(new AppSettings { ElevenLabsKey = "synthetic", VoiceId = "synthetic" }, rejectedHandler);
            bool rejectedStarted = false;
            rejectedVoice.SpeakingChanged += speakingNow => rejectedStarted |= speakingNow;
            var rejectedReply = new ReplyPresentation(this, "米亞", ct, voice: rejectedVoice);
            await rejectedReply.CompleteAsync("語音測試。");
            checks["rejected_cloud_key_shows_text_without_audio"] =
                !rejectedStarted && !rejectedVoice.IsSpeaking && rejectedReply.VoiceError is not null &&
                rejectedReply.VisibleText == "語音測試。";

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
