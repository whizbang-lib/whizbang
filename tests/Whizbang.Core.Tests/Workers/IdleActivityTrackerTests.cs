using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4a of zero-idle-polling — locks the
/// <see cref="IdleActivityTracker"/> contract.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>Construction primes the timer to "now"; a fresh pod isn't idle.</description></item>
/// <item><description><see cref="IdleActivityTracker.Touch(string)"/> resets the timer to "now" from the injected <see cref="TimeProvider"/>.</description></item>
/// <item><description><see cref="IdleActivityTracker.TimeSinceLastActivity"/> advances with the injected clock between touches.</description></item>
/// <item><description><see cref="IdleActivityTracker.LastActivitySource"/> captures the source string passed to the most recent touch.</description></item>
/// <item><description>Null source throws (defensive — coding bug, not a runtime condition).</description></item>
/// <item><description>Empty source is allowed (operators can opt out of source attribution).</description></item>
/// <item><description>Repeated touches with monotonically advancing clock all land — no torn reads.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/idle-activity-tracking</docs>
public class IdleActivityTrackerTests {

  [Test]
  public async Task Construction_PrimesTimerToNowAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    var tracker = new IdleActivityTracker(time);

    await Assert.That(tracker.TimeSinceLastActivity).IsEqualTo(TimeSpan.Zero)
      .Because("A freshly-started pod isn't idle — BackupTickCoordinator must not engage POLLING immediately after process start.");
    await Assert.That(tracker.LastActivitySource).IsEqualTo("startup")
      .Because("Captured at construction so diagnostics can distinguish 'never touched' from 'touched but a long time ago'.");
  }

  [Test]
  public async Task TimeSinceLastActivity_AdvancesWithClockAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);

    time.Advance(TimeSpan.FromSeconds(45));

    await Assert.That(tracker.TimeSinceLastActivity).IsEqualTo(TimeSpan.FromSeconds(45))
      .Because("The tracker reads from the injected TimeProvider on every TimeSinceLastActivity access, so deterministic clock advancement is reflected.");
  }

  [Test]
  public async Task Touch_ResetsTimerToCurrentClockAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);
    time.Advance(TimeSpan.FromMinutes(5));

    tracker.Touch("claim");

    await Assert.That(tracker.TimeSinceLastActivity).IsEqualTo(TimeSpan.Zero)
      .Because("Touch is the BackupTickCoordinator's ASLEEP→POLLING reverse trigger — it must zero the idle gauge so the coordinator transitions back to ASLEEP on its next state check.");
  }

  [Test]
  public async Task Touch_CapturesSourceStringAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);

    tracker.Touch("notify");

    await Assert.That(tracker.LastActivitySource).IsEqualTo("notify")
      .Because("Diagnostics need to know which hook last fired so operators can correlate 'I am alive' signals with worker activity.");
  }

  [Test]
  public async Task Touch_LastActivityAt_MatchesCurrentClockAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);
    var advanced = TimeSpan.FromSeconds(123);
    time.Advance(advanced);

    tracker.Touch("stamp");

    await Assert.That(tracker.LastActivityAt).IsEqualTo(DateTimeOffset.Parse("2026-06-04T15:02:03Z", System.Globalization.CultureInfo.InvariantCulture))
      .Because("LastActivityAt must report the exact clock instant the touch was processed so diagnostics can correlate activity to wall-clock log lines.");
  }

  [Test]
  public async Task Touch_NullSource_ThrowsAsync() {
    var tracker = new IdleActivityTracker();

    await Assert.That(() => tracker.Touch(null!))
      .Throws<ArgumentNullException>()
      .Because("Defensive: null source is a coding bug, not a runtime condition the tracker should silently absorb.");
  }

  [Test]
  public async Task Touch_EmptySource_AllowedAsync() {
    var tracker = new IdleActivityTracker();

    tracker.Touch(string.Empty);

    await Assert.That(tracker.LastActivitySource).IsEqualTo(string.Empty)
      .Because("Empty source is a legitimate operator opt-out from attribution; the tracker should not impose a non-empty constraint.");
  }

  [Test]
  public async Task Touch_RepeatedTouches_AllReflectInStateAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);

    tracker.Touch("first");
    time.Advance(TimeSpan.FromSeconds(5));
    tracker.Touch("second");
    time.Advance(TimeSpan.FromSeconds(10));
    tracker.Touch("third");

    await Assert.That(tracker.LastActivitySource).IsEqualTo("third")
      .Because("Most recent touch wins for both source and timer — no accumulation, no averaging.");
    await Assert.That(tracker.TimeSinceLastActivity).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task NoTimeProvider_FallsBackToSystemAsync() {
    // Tracker constructed without a TimeProvider should still function; we just
    // can't assert deterministic durations on it. The contract is "system clock
    // by default" — the unit test verifies it constructs and TimeSinceLastActivity
    // produces a non-negative reading.
    var tracker = new IdleActivityTracker(timeProvider: null);

    var elapsed = tracker.TimeSinceLastActivity;

    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.Zero)
      .Because("Wall-clock TimeProvider must produce monotonically non-negative durations relative to construction time.");
  }
}
