// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// A health probe must always answer, even when a source does not.
/// </summary>
/// <remarks>
/// <para>
/// The policy already states that liveness is Healthy for every component state, so that a
/// dependency fault never restarts a pod. That guarantee is worth nothing if the probe never
/// returns: the aggregator awaited every source before mapping any state, so a single source that
/// hung took the whole response with it and the mapping never ran.
/// </para>
/// <para>
/// Observed consequence: four services restarting every few minutes for 39 hours. The containers
/// exited 0, because kubelet was killing them on a liveness timeout rather than the process
/// crashing, and CPU sat near idle throughout. A readiness probe that hangs holds a pod out of
/// rotation; a liveness probe that hangs restarts it forever.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/health-probes</docs>
[Category("Health")]
public class HealthProbeMustAlwaysAnswerTests {
  // Every deadline in this class runs on a fake clock: a test decides when a source's deadline elapses by advancing
  // it, so nothing here depends on how fast the machine is or how busy its thread pool is.
  private static readonly TimeSpan _sourceTimeout = TimeSpan.FromSeconds(2);

  [Test]
  public async Task AHangingSourceDoesNotStopTheProbeAnsweringAsync() {
    var clock = new DeadlineClock();
    var aggregator = _aggregator(clock, new HangingSource("transport"));

    var evaluation = aggregator.EvaluateAsync(HealthProbe.Liveness, CancellationToken.None).AsTask();
    clock.Advance(_sourceTimeout);
    var result = await evaluation;

    // The whole point: an answer, not a hang. Without the deadline this awaits forever and kubelet
    // eventually kills the process.
    await Assert.That(result.Components).IsNotEmpty();
  }

  [Test]
  public async Task LivenessStaysHealthyWhenASourceHangsAsync() {
    var clock = new DeadlineClock();
    var aggregator = _aggregator(clock, new HangingSource("transport"));

    var evaluation = aggregator.EvaluateAsync(HealthProbe.Liveness, CancellationToken.None).AsTask();
    clock.Advance(_sourceTimeout);
    var result = await evaluation;

    // The policy says liveness never fails on a dependency state. An unreachable dependency must
    // not restart the pod, and neither must an unresponsive one.
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy)
      .Because("a hung dependency probe is a dependency problem, not a reason to kill a process "
             + "that is otherwise running");
  }

  [Test]
  public async Task ATimedOutSourceIsReportedAsFaultedAndNamedAsync() {
    var clock = new DeadlineClock();
    var aggregator = _aggregator(clock, new HangingSource("transport"));

    var evaluation = aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None).AsTask();
    clock.Advance(_sourceTimeout);
    var result = await evaluation;

    var report = result.Components.Single(c => c.Component == "transport");
    // Silently reporting Healthy would hide the very thing that needs looking at.
    await Assert.That(report.State).IsEqualTo(ComponentState.Faulted);
    await Assert.That(report.Detail).IsNotNull();
  }

  [Test]
  public async Task AHealthySourceIsUnaffectedByAHangingOneAsync() {
    var clock = new DeadlineClock();
    var aggregator = _aggregator(
      clock, new HangingSource("transport"), new FastSource("schema", ComponentState.Operational));

    var evaluation = aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None).AsTask();
    // The healthy source answers on a thread of its own. Advancing the clock before its answer is taken would time it
    // out too, which is a test that raced, not a source that hung; so wait until only the hanging source is waiting.
    await clock.AnswersTakenAsync(stillWaiting: 1);
    clock.Advance(_sourceTimeout);
    var result = await evaluation;

    var schema = result.Components.Single(c => c.Component == "schema");
    await Assert.That(schema.State).IsEqualTo(ComponentState.Operational)
      .Because("one unresponsive source must not cost the diagnosis of every other component");
  }

  [Test]
  public async Task SeveralHangingSourcesDoNotAddUpAsync() {
    var clock = new DeadlineClock();
    var aggregator = _aggregator(
      clock,
      new HangingSource("a"), new HangingSource("b"), new HangingSource("c"),
      new HangingSource("d"), new HangingSource("e"));
    var started = clock.GetUtcNow();

    var evaluation = aggregator.EvaluateAsync(HealthProbe.Liveness, CancellationToken.None).AsTask();

    // Every source's deadline is armed before any of them can elapse. Asked one after the other, only the first
    // would be: the second deadline would start when the first one ran out, and five unresponsive sources at two
    // seconds each would be ten seconds, past the probe's own timeout.
    await Assert.That(clock.ArmedDeadlines).IsEqualTo(5)
      .Because("a per-source timeout that accumulates still times the probe out once enough "
             + "sources are unresponsive");

    // One deadline's worth of time answers the probe for all five.
    clock.Advance(_sourceTimeout);
    var result = await evaluation;

    await Assert.That(clock.GetUtcNow() - started).IsEqualTo(_sourceTimeout);
    await Assert.That(result.Components.Count).IsEqualTo(5);
    await Assert.That(result.Components.All(c => c.State == ComponentState.Faulted)).IsTrue()
      .Because("each unresponsive source is reported as faulted once its own deadline elapses");
  }

  [Test]
  public async Task NoSourceRunsOnTheProbesOwnStackAsync() {
    // A source does not have to await anything before it hangs. A library call made synchronously, or an asynchronous
    // one waited on with .Result, blocks inside ReportAsync before it has returned a task the deadline could bound.
    // Called on the probe's own stack, such a source holds the probe there: no deadline is armed for it or for any
    // source after it, and the probe never answers.
    var source = new CallerStackSource("transport");
    var aggregator = _aggregator(new DeadlineClock(), source);

    _ = await CallerStackSource.CallAsync(() => aggregator.EvaluateAsync(HealthProbe.Liveness, CancellationToken.None));

    await Assert.That(source.RanOnTheCallersStack).IsFalse()
      .Because("a source that blocks before it returns must block a thread of its own, not the probe");
  }

  [Test]
  public async Task ASourceThatBlocksBeforeItReturnsIsTimedOutAsync() {
    var clock = new DeadlineClock();
    var blocking = new BlockingSource("transport");
    var aggregator = _aggregator(clock, blocking, new FastSource("schema", ComponentState.Operational));

    try {
      var evaluation = aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None).AsTask();
      // The source is now blocked inside ReportAsync, with no task returned for the deadline to bound, and the healthy
      // source's answer is taken before the clock moves.
      await blocking.Entered;
      await clock.AnswersTakenAsync(stillWaiting: 1);
      clock.Advance(_sourceTimeout);
      var result = await evaluation;

      await Assert.That(result.Components.Single(c => c.Component == "transport").State)
        .IsEqualTo(ComponentState.Faulted)
        .Because("a source blocked before it returns is unresponsive like any other, and is reported so");
      await Assert.That(result.Components.Single(c => c.Component == "schema").State)
        .IsEqualTo(ComponentState.Operational);
    } finally {
      blocking.Release();
    }
  }

  private static WhizbangHealthAggregator _aggregator(TimeProvider clock, params IWhizbangHealthSource[] sources)
    => new(
      sources, new WhizbangHealthOptions { SourceTimeout = _sourceTimeout },
      Whizbang.Core.Startup.NullStartupStageReporter.Instance, clock);

  /// <summary>
  /// A fake clock that counts the deadlines armed on it and the deadlines disarmed. The aggregator's wait disarms a
  /// source's deadline as soon as that source's answer is taken, and a source that had already answered when its wait
  /// began never arms one; so once every deadline but those of the sources still waiting is disarmed, every answer
  /// there is has been taken.
  /// </summary>
  private sealed class DeadlineClock() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
    private readonly Channel<bool> _disarmedSignal = Channel.CreateUnbounded<bool>();
    private int _armed;
    private int _disarmed;

    public int ArmedDeadlines => Volatile.Read(ref _armed);

    /// <summary>
    /// Completes once every deadline armed so far is disarmed except <paramref name="stillWaiting"/>. Call it after the
    /// evaluation has started, when every deadline it arms is armed.
    /// </summary>
    public async Task AnswersTakenAsync(int stillWaiting) {
      var answered = ArmedDeadlines - stillWaiting;
      while (Volatile.Read(ref _disarmed) < answered) {
        _ = await _disarmedSignal.Reader.ReadAsync();
      }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Interlocked.Increment(ref _armed);
      return new Deadline(base.CreateTimer(callback, state, dueTime, period), this);
    }

    private void _onDisarmed() {
      Interlocked.Increment(ref _disarmed);
      _disarmedSignal.Writer.TryWrite(true);
    }

    private sealed class Deadline(ITimer timer, DeadlineClock clock) : ITimer {
      private int _disposed;

      public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

      // The wait may dispose a deadline twice (once when the answer arrives, once more if it arrived while the wait
      // was still being set up); it is disarmed once.
      public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) {
          timer.Dispose();
          clock._onDisarmed();
        }
      }

      public ValueTask DisposeAsync() {
        Dispose();
        return ValueTask.CompletedTask;
      }
    }
  }

  /// <summary>Records whether it was asked on the stack of the thread that called the aggregator.</summary>
  private sealed class CallerStackSource(string component) : IWhizbangHealthSource {
    [ThreadStatic]
    private static bool _insideTheCall;

    /// <summary>Makes the call, marking this thread as inside it until the call returns its first task.</summary>
    public static ValueTask<AggregatedHealth> CallAsync(Func<ValueTask<AggregatedHealth>> call) {
      _insideTheCall = true;
      try {
        return call();
      } finally {
        _insideTheCall = false;
      }
    }

    public string Component { get; } = component;

    public bool? RanOnTheCallersStack { get; private set; }

    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
      RanOnTheCallersStack = _insideTheCall;
      return new(new ComponentHealth(ComponentState.Operational));
    }
  }

  /// <summary>Blocks its calling thread inside <c>ReportAsync</c>, before returning a task, until released.</summary>
  private sealed class BlockingSource(string component) : IWhizbangHealthSource {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Component { get; } = component;

    /// <summary>Completes once the source is blocked.</summary>
    public Task Entered => _entered.Task;

    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      // Deliberately synchronous and deaf to the token: the blocking library call this stands in for is both.
      _release.Task.Wait(CancellationToken.None);
      return new(new ComponentHealth(ComponentState.Operational));
    }

    /// <summary>Unblocks the source's thread.</summary>
    public void Release() => _release.TrySetResult();
  }

  private sealed class HangingSource(string component) : IWhizbangHealthSource {
    public string Component { get; } = component;

    public async ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
      // Deliberately ignores the token, which is the real-world case: a network probe blocked in a
      // library that does not observe cancellation.
      await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
      return new ComponentHealth(ComponentState.Operational);
    }
  }

  private sealed class FastSource(string component, ComponentState state) : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
      => new(new ComponentHealth(state));
  }
}
