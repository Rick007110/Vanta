using Vanta.Core.Update;
using Xunit;

namespace Vanta.Tests;

public class UpdatePolicyTests
{
    private DateTime _now = new(2026, 9, 27, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Schedule_is_30_minutes_and_focus_checks_are_throttled_to_5_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), UpdatePolicy.Interval);
        var p = new UpdatePolicy(() => _now);
        Assert.True(p.TryFocusCheck());                 // nothing checked yet
        _now = _now.AddMinutes(2); Assert.False(p.TryFocusCheck());
        _now = _now.AddMinutes(2.9); Assert.False(p.TryFocusCheck());
        _now = _now.AddMinutes(0.2); Assert.True(p.TryFocusCheck());   // 5.1 min after the last check
        _now = _now.AddMinutes(4); p.MarkChecked();     // timer/start check also counts
        _now = _now.AddMinutes(4); Assert.False(p.TryFocusCheck());
        _now = _now.AddMinutes(1.5); Assert.True(p.TryFocusCheck());
    }

    [Fact]
    public void A_version_is_offered_once_per_session_and_never_again_after_Later()
    {
        var p = new UpdatePolicy(() => _now);
        Assert.True(p.ShouldOffer("0.2.2", manual: false));
        Assert.False(p.ShouldOffer("0.2.2", manual: false));     // periodic/focus check: toast is not re-shown
        p.Dismiss("0.2.2");
        Assert.False(p.ShouldOffer("0.2.2", manual: false));
        Assert.False(p.ShouldShowPending("0.2.2", manual: false));
        Assert.True(p.ShouldOffer("0.2.2", manual: true));       // "Controleer op updates" always shows it
        Assert.True(p.ShouldOffer("0.2.3", manual: false));      // a newer release is offered again
        Assert.True(p.ShouldShowPending("0.2.3", manual: false));
        Assert.False(p.ShouldShowPending("0.2.3", manual: false));
    }

    [Fact]
    public void Progress_throttle_allows_about_10_per_second_and_always_the_final_report()
    {
        var t = new ProgressThrottle(TimeSpan.FromMilliseconds(100), () => _now);
        int sent = 0;
        for (int i = 1; i <= 1000; i++)                          // 1000 chunks over 2 seconds
        {
            _now = _now.AddMilliseconds(2);
            if (t.ShouldReport(i / 1000.0)) sent++;
        }
        Assert.InRange(sent, 19, 22);
        Assert.False(t.ShouldReport(1));                         // 100% only once
        var t2 = new ProgressThrottle(TimeSpan.FromMilliseconds(100), () => _now);
        Assert.True(t2.ShouldReport(0.5));
        _now = _now.AddSeconds(1);
        Assert.False(t2.ShouldReport(0.4));                      // never backwards
        Assert.True(t2.ShouldReport(1));
    }
}
