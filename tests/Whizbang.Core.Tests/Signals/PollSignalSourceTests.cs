using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Unit tests for <see cref="BasePollSignalSource{TSignal}"/> — the base class used by concrete
/// pull sources that periodically detect a condition and raise a signal into the bus. Uses
/// <see cref="FakeTimeProvider"/> so the polling interval is deterministic (no <c>Task.Delay</c>).
/// </summary>
public class PollSignalSourceTests {
  private readonly record struct PollProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private class FakePollSource(FakeTimeProvider clock, TimeSpan interval)
    : BasePollSignalSource<PollProbe>(clock, interval) {
    public int DetectCallCount { get; private set; }
    public bool DetectResult { get; set; } = true;

    protected override ValueTask<bool> DetectAsync(CancellationToken cancellationToken) {
      DetectCallCount++;
      return ValueTask.FromResult(DetectResult);
    }
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task StartAsync_NullSink_ThrowsAsync() {
    var source = new FakePollSource(new FakeTimeProvider(), TimeSpan.FromMilliseconds(100));
    await Assert.That(() => source.StartAsync(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Ctor_NullClock_ThrowsAsync() {
    await Assert.That(() => new FakePollSource(null!, TimeSpan.FromMilliseconds(100)))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Ctor_NonPositiveInterval_ThrowsAsync() {
    var clock = new FakeTimeProvider();
    await Assert.That(() => new FakePollSource(clock, TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new FakePollSource(clock, TimeSpan.FromMilliseconds(-1))).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Tick_DetectTrue_RaisesSignalOnSinkAsync() {
    var clock = new FakeTimeProvider();
    var source = new FakePollSource(clock, TimeSpan.FromSeconds(1));
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(source.DetectCallCount).IsEqualTo(1);
    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task Tick_DetectFalse_DoesNotRaiseAsync() {
    var clock = new FakeTimeProvider();
    var source = new FakePollSource(clock, TimeSpan.FromSeconds(1)) { DetectResult = false };
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(source.DetectCallCount).IsEqualTo(1);
    await Assert.That(sink.Received).IsEqualTo(0);
  }

  [Test]
  public async Task Tick_BeforeStart_ThrowsInvalidOperationAsync() {
    var source = new FakePollSource(new FakeTimeProvider(), TimeSpan.FromSeconds(1));
    // Ticking before start would raise into a null sink — surface the bug loudly rather than no-op.
    await Assert.That(async () => await source.TickForTestsAsync(CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Interval_ReflectsCtorArgumentAsync() {
    var interval = TimeSpan.FromSeconds(3);
    var source = new FakePollSource(new FakeTimeProvider(), interval);

    await Assert.That(source.Interval).IsEqualTo(interval);
  }

  private sealed class ReschedulingFakePollSource(FakeTimeProvider clock, TimeSpan interval)
    : FakePollSource(clock, interval) {
    public void ReschedulePublic(TimeSpan next) => Reschedule(next);
  }

  [Test]
  public async Task Reschedule_UpdatesTheIntervalPropertyAsync() {
    var source = new ReschedulingFakePollSource(new FakeTimeProvider(), TimeSpan.FromSeconds(5));
    await source.StartAsync(new CountingSink());

    source.ReschedulePublic(TimeSpan.FromMilliseconds(500));

    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(500));
  }

  [Test]
  public async Task Reschedule_NonPositive_ThrowsAsync() {
    var source = new ReschedulingFakePollSource(new FakeTimeProvider(), TimeSpan.FromSeconds(5));
    await source.StartAsync(new CountingSink());

    await Assert.That(() => source.ReschedulePublic(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Reschedule_BeforeStart_UpdatesIntervalWithoutTimerAsync() {
    // Rescheduling before StartAsync just records the new interval; the timer isn't created yet.
    var source = new ReschedulingFakePollSource(new FakeTimeProvider(), TimeSpan.FromSeconds(5));

    source.ReschedulePublic(TimeSpan.FromMilliseconds(200));

    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(200));
  }
}
