using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mia;

// Each named emotion has two authored frames plus a bounded motion profile.
// Gesture is a fallback if an optional emotion atlas is unavailable.
public sealed record SpeechAnimation(string Tag, string Gesture, double Period, double Tilt, double Lean, double Breath)
{
    public static readonly SpeechAnimation Default = new("", "speaking", 2.4, 0, 0, .008);
    public static readonly IReadOnlyList<SpeechAnimation> All = new[]
    {
        new SpeechAnimation("happy", "proud-nod", 2.2, 1.3, 0, .010),
        new SpeechAnimation("excited", "speaking", .8, 2.0, 0, .018),
        new SpeechAnimation("playful", "ear-groom", 1.6, 3.0, 1, .012),
        new SpeechAnimation("amazed", "listening", 1.3, 1.0, -2, .014),
        new SpeechAnimation("powerful", "proud-nod", 2.8, .7, -1, .012),
        new SpeechAnimation("proud", "proud-nod", 3.2, 1.2, -1.5, .009),
        new SpeechAnimation("optimistic", "speaking", 2.5, 1.8, 1, .010),
        new SpeechAnimation("startled", "listening", .7, 1.3, -3, .018),
        new SpeechAnimation("angry", "speaking", 1.2, 1.0, 3, .012),
        new SpeechAnimation("mad", "speaking", 1.0, 1.5, 3.5, .014),
        new SpeechAnimation("aggressive", "speaking", .9, 1.7, 4, .016),
        new SpeechAnimation("bitter", "collar-tidy", 3.8, .5, -2, .005),
        new SpeechAnimation("critical", "listening", 2.9, .9, 2, .006),
        new SpeechAnimation("repelled", "listening", 2.4, 1.2, -4, .007),
        new SpeechAnimation("anxious", "hand-warm", 1.1, 1.0, 1, .013),
        new SpeechAnimation("stressed", "collar-tidy", 1.5, 1.2, 2, .012),
        new SpeechAnimation("scared", "hand-warm", .8, .8, -2, .014),
        new SpeechAnimation("threatened", "hand-warm", 1.3, .6, -3, .011),
        new SpeechAnimation("vulnerable", "hand-warm", 3.3, .6, 1, .006),
        new SpeechAnimation("confused", "listening", 2.7, 3.5, 0, .007),
        new SpeechAnimation("busy", "collar-tidy", 1.7, 1.4, 1, .009),
        new SpeechAnimation("tired", "yawn", 5.0, .7, 2, .005),
        new SpeechAnimation("bored", "listening", 4.5, 1.5, -1, .004),
        new SpeechAnimation("distant", "listening", 5.5, .4, -2, .003),
        new SpeechAnimation("despair", "hand-warm", 5.2, .5, 3, .004),
        new SpeechAnimation("let down", "collar-tidy", 4.8, .7, 2.5, .005),
        new SpeechAnimation("sad", "hand-warm", 4.2, .8, 2, .006),
        new SpeechAnimation("peaceful", "listening", 4.0, .6, 0, .007),
        new SpeechAnimation("content", "proud-nod", 4.4, .8, -.5, .008),
        new SpeechAnimation("curious", "listening", 2.6, 2.8, 1.5, .009),
        new SpeechAnimation("thoughtful", "collar-tidy", 3.9, 1.4, 1, .006),
        new SpeechAnimation("trusting", "hand-warm", 3.6, 1.1, -.5, .008),
        new SpeechAnimation("sympathetic", "hand-warm", 3.7, 1.0, 1.5, .007),
        new SpeechAnimation("whispers", "hand-warm", 3.4, .6, 2.5, .004),
        new SpeechAnimation("softly", "listening", 3.5, .8, 1, .005),
        new SpeechAnimation("quietly", "listening", 4.1, .4, .5, .004),
        new SpeechAnimation("shouts", "speaking", 1.0, 1.2, 3, .020),
        new SpeechAnimation("laughs", "speaking", .6, 2.4, -1, .022),
        new SpeechAnimation("sighs", "collar-tidy", 4.6, .4, 2, .012),
        new SpeechAnimation("gasps", "listening", .9, .7, -2.5, .020),
        new SpeechAnimation("clears throat", "collar-tidy", 1.8, .5, 1.5, .010),
        new SpeechAnimation("crying", "hand-warm", 1.9, 1.1, 3, .016)
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whispering"]="whispers", ["hushed"]="whispers", ["shouting"]="shouts",
        ["laughing"]="laughs", ["chuckles"]="laughs", ["sighing"]="sighs",
        ["gasping"]="gasps", ["starts crying"]="crying", ["sobbing"]="crying",
        ["nervous"]="anxious", ["jittery"]="anxious", ["frazzled"]="stressed",
        ["puzzled"]="confused", ["intrigued"]="curious", ["hopeful"]="optimistic",
        ["triumphant"]="proud", ["smug"]="proud", ["mischievous"]="playful",
        ["mischievously"]="playful", ["disgusted"]="repelled", ["disbelief"]="amazed",
        ["gentle"]="softly", ["calm"]="peaceful", ["disappointed"]="let down",
        ["sarcastic"]="bitter", ["sarcastically"]="bitter", ["soothing"]="sympathetic",
        ["reassuring"]="sympathetic", ["affectionate"]="trusting", ["loving"]="trusting",
        ["embarrassed"]="vulnerable", ["shy"]="vulnerable", ["surprised"]="amazed",
        ["booming"]="shouts", ["barely audible"]="quietly", ["threatening"]="aggressive"
    };

    public static SpeechAnimation Resolve(string text)
    {
        var matches = Regex.Matches(Services.SpeechTags.WithoutDelivery(text), @"\[(?:audio:\s*)?([^\[\]\r\n]+)\]", RegexOptions.IgnoreCase);
        SpeechAnimation result = Default;
        foreach (Match match in matches)
        {
            string cue = match.Groups[1].Value.ToLowerInvariant();
            // First explicit emotion wins within a compound cue, followed by delivery.
            var candidates = All.Select(p => (Name: p.Tag, Profile: p))
                .Concat(Aliases.Select(a => (Name: a.Key, Profile: All.First(p => p.Tag == a.Value))));
            var found = candidates.Where(c => Regex.IsMatch(cue, @"\b" + Regex.Escape(c.Name) + @"\b"))
                .OrderBy(c => All.ToList().IndexOf(c.Profile)).FirstOrDefault();
            if (found.Profile is not null) result = found.Profile;
        }
        return result;
    }

    public (double Tilt, double ScaleY) Motion(double seconds)
    {
        double phase = seconds / Period * Math.PI * 2;
        return (Lean + Math.Sin(phase) * Tilt, 1 + Math.Sin(phase) * Breath);
    }
}
