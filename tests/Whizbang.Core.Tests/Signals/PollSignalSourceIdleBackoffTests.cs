using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// That a pull source polling an idle store stretches its own cadence, and snaps back the moment
/// work appears or the cadence is reset from outside.
/// </summary>
/// <remarks>
/// With every queue empty, the busiest databases were committing well over a hundred transactions
/// a second from poll loops alone, a third of the server's CPU with nothing to do. A source that
/// has found nothing for a few ticks polls less often, doubling toward a ceiling, and a hit or an
/// external reschedule (the push transport flipping, for one) returns it to its base interval.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public class PollSignalSourceIdleBackoffTests {
  private readonly record struct IdleProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class FakePollSource(FakeTimeProvider clock, TimeSpan interval, PollIdleBackoff? backoff)
    : BasePollSignalSource<IdleProbe>(clock, interval, backoff) {
    public bool DetectResult { get; set; }
    public void RescheduleForTests(TimeSpan interval) => Reschedule(interval);
    protected override ValueTask<bool> DetectAsync(CancellationToken cancellationToken) =>
      ValueTask.FromResult(DetectResult);
  }

  private sealed class NullSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  private static readonly TimeSpan _base = TimeSpan.FromSeconds(5);
  private static readonly PollIdleBackoff _backoff = new(AfterEmptyTicks: 3, Ceiling: TimeSpan.FromSeconds(40));

  private static async Task<FakePollSource> _startedAsync(PollIdleBackoff? backoff, bool detect = false) {
    var source = new FakePollSource(new FakeTimeProvider(), _base, backoff) { DetectResult = detect };
    await source.StartAsync(new NullSink());
    return source;
  }

  private static async Task _tickAsync(FakePollSource source, int times) {
    for (var i = 0; i < times; i++) {
      await source.TickForTestsAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task EmptyTicksPastTheThreshold_DoubleTheIntervalUpToTheCeilingAsync() {
    var source = await _startedAsync(_backoff);

    await _tickAsync(source, 3);
    await Assert.That(source.Interval).IsEqualTo(_base)
      .Because("a few empty ticks are ordinary between bursts and must not slow the source");

    await _tickAsync(source, 1);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(10));
    await _tickAsync(source, 1);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(20));
    await _tickAsync(source, 1);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(40));
    await _tickAsync(source, 5);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(40))
      .Because("the ceiling bounds how long a source can go without looking");
  }

  [Test]
  public async Task WorkFound_ReturnsToTheBaseIntervalAsync() {
    var source = await _startedAsync(_backoff);
    await _tickAsync(source, 6);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(40));

    source.DetectResult = true;
    await _tickAsync(source, 1);

    await Assert.That(source.Interval).IsEqualTo(_base)
      .Because("once work is flowing the source must look at its base cadence again");
    source.DetectResult = false;
    await _tickAsync(source, 3);
    await Assert.That(source.Interval).IsEqualTo(_base)
      .Because("the empty streak restarts from zero after a hit");
  }

  [Test]
  public async Task RescheduleFromOutside_ResetsTheStreakAndBecomesTheNewBaseAsync() {
    var source = await _startedAsync(_backoff);
    await _tickAsync(source, 5);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(20));

    source.RescheduleForTests(TimeSpan.FromMilliseconds(500));

    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(500))
      .Because("the push transport going away tightens the cadence and the backoff must not undo that");
    await _tickAsync(source, 4);
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromSeconds(1))
      .Because("the backoff doubles from the new base, not the original one");
  }

  [Test]
  public async Task NoBackoffConfigured_KeepsTheIntervalAsync() {
    var source = await _startedAsync(backoff: null);

    await _tickAsync(source, 50);

    await Assert.That(source.Interval).IsEqualTo(_base);
  }

  [Test]
  public async Task ABackoffThatCannotStretchIsRefusedAsync() {
    await Assert.That(() => new PollIdleBackoff(AfterEmptyTicks: 0, Ceiling: TimeSpan.FromSeconds(1)))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new PollIdleBackoff(AfterEmptyTicks: 1, Ceiling: TimeSpan.Zero))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ACeilingBelowTheBaseNeverStretchesAsync() {
    var source = await _startedAsync(new PollIdleBackoff(AfterEmptyTicks: 1, Ceiling: TimeSpan.FromSeconds(1)));

    await _tickAsync(source, 5);

    await Assert.That(source.Interval).IsEqualTo(_base)
      .Because("a ceiling below the base is a request to never poll slower than configured");
  }
}
