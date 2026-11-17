using System.Runtime.InteropServices;
using System.Text;
using Mia.Services;

namespace Mia;

public sealed partial class PetWindow
{
    private readonly InteractionMemory? _interactions = InteractionMemory.Desktop();
    private readonly Dictionary<string, DateTimeOffset> _interactionCooldowns = new();
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset Through, string? Group)> _intervals = new();
    private string _interactionGeneration = "";
    private string? _ballSession;
    private DateTimeOffset _lastTracked, _lastDirectActivity, _lastBallInput, _nearSince, _nextDesktopCheck;
    private bool _desktopActive = true, _nearCharacter, _dwellRecorded;
    private NativeDesktop.Point _nearCursor;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDesktopInformation(IntPtr desktop, int index, StringBuilder value, int length, out int required);

    private static bool ActiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetDesktopInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    private void RefreshInteractionGeneration()
    {
        string generation = _interactions?.Generation() ?? "";
        if (_interactionGeneration == generation) return;
        _interactionGeneration = generation;
        _intervals.Clear();
        _interactionCooldowns.Clear();
        _lastDirectActivity = _lastBallInput = _nearSince = _lastTracked = default;
        _nearCharacter = _dwellRecorded = false;
        _ballSession = null;
    }

    private void RecordDesktopInteraction(string kind, string? group = null, double cooldown = 0)
    {
        if (_interactions is null || Paused || !_desktopActive) return;
        RefreshInteractionGeneration();
        if (_interactionGeneration.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        _lastDirectActivity = now;
        if (_interactionCooldowns.TryGetValue(kind, out var expires) && now < expires) return;
        _interactionCooldowns[kind] = now.AddSeconds(cooldown);
        _interactions.Record(new(Guid.NewGuid().ToString("N"), now, "PC", kind, group), _interactionGeneration);
    }

    internal void MarkBallInput(bool released, bool thrown = false)
    {
        if (_interactions is null || Paused || !_desktopActive) return;
        RefreshInteractionGeneration();
        if (_interactionGeneration.Length == 0) return;
        _lastBallInput = _lastDirectActivity = DateTimeOffset.UtcNow;
        if (_ballSession is null)
        {
            _ballSession = Guid.NewGuid().ToString("N");
            RecordDesktopInteraction("ball_session", _ballSession);
        }
        if (released && thrown) RecordDesktopInteraction("ball_throw", _ballSession);
    }

    private void SealBallInteraction()
    {
        FlushInterval("ball_elapsed");
        FlushInterval("ball_active");
        FlushInterval("ball_solo");
        _ballSession = null;
        _lastBallInput = default;
    }

    private void TrackDesktopInteraction(DateTime now, NativeDesktop.Point cursor, double width, double height)
    {
        if (_interactions is null) return;
        var utc = new DateTimeOffset(now);
        if (utc >= _nextDesktopCheck)
        {
            _desktopActive = ActiveDesktop();
            _nextDesktopCheck = utc.AddSeconds(1);
            RefreshInteractionGeneration();
        }
        TimeSpan elapsed = utc - _lastTracked;
        DateTimeOffset previous = _lastTracked;
        _lastTracked = utc;
        if (!_desktopActive || Paused || elapsed.TotalSeconds is <= 0 or > 2 || _interactionGeneration.Length == 0)
        {
            FlushInteractionIntervals();
            _nearCharacter = _dwellRecorded = false;
            _lastBallInput = _lastDirectActivity = default;
            return;
        }

        double distance = CursorAttention.Distance(cursor.X, cursor.Y, x, y, width, height);
        if (!dragging && !_nearCharacter && distance <= 1)
        {
            _nearCharacter = true;
            _nearSince = utc;
            _nearCursor = cursor;
            _dwellRecorded = false;
            RecordDesktopInteraction("cursor_near", cooldown: 8);
        }
        else if (_nearCharacter && distance > 1.25)
            _nearCharacter = false;
        else if (_nearCharacter)
        {
            if (cursor.X != _nearCursor.X || cursor.Y != _nearCursor.Y) _lastDirectActivity = utc;
            _nearCursor = cursor;
            if (!_dwellRecorded && utc - _nearSince >= TimeSpan.FromSeconds(1.5))
            {
                _dwellRecorded = true;
                RecordDesktopInteraction("cursor_dwell", cooldown: 8);
            }
        }

        bool ballActive = ball is not null && !InteractionOpen &&
            (ball.Held || utc - _lastBallInput < TimeSpan.FromSeconds(15));
        bool ballSolo = ball is not null && !ball.Held && !_ballActiveControl && !ballActive &&
            (ball.PetHeld || utc < kickUntil || autonomous == "追到球下方準備頂球");
        TrackInterval("presence", previous, utc, null, 60);
        TrackInterval("interaction_active", previous,
            ballActive || utc - _lastDirectActivity < TimeSpan.FromSeconds(30) ? utc : previous, null, 30);
        TrackInterval("ball_elapsed", previous, ball is not null ? utc : previous, _ballSession, 15);
        TrackInterval("ball_active", previous, ballActive ? utc : previous, _ballSession, 15);
        TrackInterval("ball_solo", previous, ballSolo ? utc : previous, _ballSession, 15);
    }

    private bool _ballActiveControl => InteractionOpen || speaking || WorkingInCompanion || dragging;

    private void TrackInterval(string kind, DateTimeOffset from, DateTimeOffset to, string? group, double checkpoint)
    {
        if (to <= from) { FlushInterval(kind); return; }
        if (!_intervals.TryGetValue(kind, out var interval) || interval.Group != group || interval.Through != from)
        {
            FlushInterval(kind);
            interval = (from, from, group);
        }
        _intervals[kind] = (interval.Start, to, group);
        if ((to - interval.Start).TotalSeconds >= checkpoint) FlushInterval(kind);
    }

    private void FlushInterval(string kind)
    {
        if (_intervals.Remove(kind, out var interval))
            _interactions?.Record(new(Guid.NewGuid().ToString("N"), interval.Start, "PC", kind,
                interval.Group, (interval.Through - interval.Start).TotalSeconds), _interactionGeneration);
    }

    private void FlushInteractionIntervals()
    {
        foreach (var kind in _intervals.Keys.ToArray()) FlushInterval(kind);
    }
}
