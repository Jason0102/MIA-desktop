using System;
using System.Text.Json.Serialization;

namespace Mia.Services;

public sealed class AppSettings
{
    public string OpenAiModel { get; set; } = "gpt-6.1-sol";
    public string ElevenLabsModel { get; set; } = "eleven_v4";
    public string VoiceId { get; set; } = "";
    public string VoiceMaterialsPath { get; set; } = "";
    public bool InteractionHints { get; set; }
    public bool Muted { get; set; }
    public string Mode { get; set; } = "companion";
    public bool StartWithWindows { get; set; }
    public double PetScale { get; set; } = 1.0;
    public double UiScale { get; set; } = 1.0;
    public string[]? AllowedScreenIds { get; set; }
    [JsonIgnore] public string OpenAiKey { get; set; } = "";
    [JsonIgnore] public string ElevenLabsKey { get; set; } = "";
}
