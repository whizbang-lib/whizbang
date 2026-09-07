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

  /// <summary>
  /// A source whose detection query always fails, recording what the base class hands to
  /// <c>OnTickError</c> and then delegating to the base implementation — which is the no-op that
  /// concrete sources inherit when they don't override it.
  /// </summary>
  private sealed class FailingPollSource(FakeTimeProvider clock, TimeSpan interval)
    : BasePollSignalSource<PollProbe>(clock, interval) {
    private readonly Lock _gate = new();
    private readonly List<Exception> _observed = [];
    private int _detectCalls;

    public bool FailNextDetect { get; set; } = true;
    public int DetectCalls => Volatile.Read(ref _detectCalls);
    public bool BaseReturnedNormally { get; private set; }
    public TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<Exception> Observed {
      get { lock (_gate) { return [.. _observed]; } }
    }

    protected override ValueTask<bool> DetectAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _detectCalls);
      return FailNextDetect
        ? throw new InvalidOperationException("detection query failed")
        : ValueTask.FromResult(true);
    }

    protected override void OnTickError(Exception ex) {
      lock (_gate) { _observed.Add(ex); }
      // The base is the crash-free default every source inherits. If it did anything other than
      // return, the flag below would never be set and the timer thread would be carrying an
      // exception a source with no override could not have handled.
      base.OnTickError(ex);
      BaseReturnedNormally = true;
      FirstError.TrySetResult();
    }
  }

  [Test]
  public async Task TimerTick_DetectionThrows_GoesToOnTickErrorAndTheSourceKeepsPollingAsync() {
    // A poll source runs on a timer callback with nobody awaiting it. If a failed detection query
    // — a dropped connection, a locked table — escaped the tick, the source would stop polling
    // for the rest of the process and the signal it reconciles would silently never fire again.
    var clock = new FakeTimeProvider();
    var source = new FailingPollSource(clock, TimeSpan.FromSeconds(1));
    var sink = new CountingSink();
    await source.StartAsync(sink);

    clock.Advance(TimeSpan.FromSeconds(1));
    await source.FirstError.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(source.Observed).Count().IsEqualTo(1);
    await Assert.That(source.Observed[0]).IsTypeOf<InvalidOperationException>();
    await Assert.That(source.Observed[0].Message).IsEqualTo("detection query failed")
      .Because("the exact detection failure has to reach the source so it can be logged");
    await Assert.That(source.BaseReturnedNormally).IsTrue()
      .Because("the inherited default must swallow the exception and return — a source that "
             + "does not override OnTickError still has to get crash-free semantics");
    await Assert.That(sink.Received).IsEqualTo(0)
      .Because("a failed detection raises nothing — a doorbell on a query that never answered "
             + "would wake every subscriber on no evidence at all");

    // The timer must still be armed after the failure.
    source.FailNextDetect = false;
    clock.Advance(TimeSpan.FromSeconds(1));

    await Assert.That(source.DetectCalls).IsEqualTo(2)
      .Because("the tick that threw must not take the polling schedule down with it");
    await Assert.That(sink.Received).IsEqualTo(1)
      .Because("once detection succeeds again the source resumes raising the signal");
  }
}
