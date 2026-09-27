namespace Vanta.Core.Update;

/// <summary>
/// When the running app checks for updates and when it offers one: at start, every <see cref="Interval"/>, and on window
/// activation at most once per <see cref="FocusThrottle"/>. A version is offered automatically at most once per session;
/// after "Later" (or closing the toast) it isn't shown again until the next start (a manual check always shows it).
/// </summary>
public sealed class UpdatePolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FocusThrottle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);

    private readonly Func<DateTime> _now;
    private readonly HashSet<string> _offered = new(), _dismissed = new(), _pendingShown = new();
    private DateTime? _lastCheck;
    private readonly object _lock = new();

    public UpdatePolicy(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Records a check that is about to run (any trigger).</summary>
    public void MarkChecked() { lock (_lock) _lastCheck = _now(); }

    /// <summary>Window focus/activate: true (and marks the check) when the last check is at least 5 minutes ago.</summary>
    public bool TryFocusCheck()
    {
        lock (_lock)
        {
            if (_lastCheck is DateTime t && _now() - t < FocusThrottle) return false;
            _lastCheck = _now();
            return true;
        }
    }

    /// <summary>Should an "update available" prompt for this version be shown? Marks it as offered when true.</summary>
    public bool ShouldOffer(string version, bool manual)
    {
        lock (_lock)
        {
            if (manual) { _offered.Add(version); return true; }
            if (_dismissed.Contains(version) || _offered.Contains(version)) return false;
            _offered.Add(version);
            return true;
        }
    }

    /// <summary>"Ready to install on next start" notice: once per version per session, never after a dismissal.</summary>
    public bool ShouldShowPending(string version, bool manual)
    {
        lock (_lock)
        {
            if (manual) return true;
            if (_dismissed.Contains(version)) return false;
            return _pendingShown.Add(version);
        }
    }

    public void Dismiss(string version) { lock (_lock) { _dismissed.Add(version); _offered.Add(version); } }
    public bool IsDismissed(string version) { lock (_lock) return _dismissed.Contains(version); }
}

/// <summary>Limits progress messages to the UI (default: at most 10 per second, always the first and the final 100%).</summary>
public sealed class ProgressThrottle
{
    private readonly TimeSpan _min;
    private readonly Func<DateTime> _now;
    private DateTime _last = DateTime.MinValue;
    private double _lastValue = -1;

    public ProgressThrottle(TimeSpan? min = null, Func<DateTime>? now = null) { _min = min ?? TimeSpan.FromMilliseconds(100); _now = now ?? (() => DateTime.UtcNow); }

    public bool ShouldReport(double p)
    {
        var now = _now();
        bool final = p >= 1 && _lastValue < 1;
        if (!final && (now - _last < _min || p <= _lastValue)) return false;
        _last = now; _lastValue = p;
        return true;
    }
}
