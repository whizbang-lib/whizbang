// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  private static WhizbangHealthAggregator _aggregator(TimeProvider clock, params IWhizbangHealthSource[] sources)
    => new(
      sources, new WhizbangHealthOptions { SourceTimeout = _sourceTimeout },
      Whizbang.Core.Startup.NullStartupStageReporter.Instance, clock);

  /// <summary>A fake clock that counts the deadlines armed on it.</summary>
  private sealed class DeadlineClock() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
    private int _armed;

    public int ArmedDeadlines => Volatile.Read(ref _armed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Interlocked.Increment(ref _armed);
      return base.CreateTimer(callback, state, dueTime, period);
    }
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
