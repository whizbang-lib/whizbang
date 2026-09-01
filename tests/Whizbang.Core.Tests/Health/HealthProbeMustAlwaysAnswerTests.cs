using Microsoft.Extensions.Diagnostics.HealthChecks;
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

  [Test]
  public async Task AHangingSourceDoesNotStopTheProbeAnsweringAsync() {
    var options = new WhizbangHealthOptions { SourceTimeout = TimeSpan.FromMilliseconds(150) };
    var aggregator = new WhizbangHealthAggregator([new HangingSource("transport")], options);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await aggregator.EvaluateAsync(HealthProbe.Liveness, cts.Token);

    // The whole point: an answer, not a hang. Without the timeout this awaits forever and kubelet
    // eventually kills the process.
    await Assert.That(result.Components).IsNotEmpty();
  }

  [Test]
  public async Task LivenessStaysHealthyWhenASourceHangsAsync() {
    var options = new WhizbangHealthOptions { SourceTimeout = TimeSpan.FromMilliseconds(150) };
    var aggregator = new WhizbangHealthAggregator([new HangingSource("transport")], options);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await aggregator.EvaluateAsync(HealthProbe.Liveness, cts.Token);

    // The policy says liveness never fails on a dependency state. An unreachable dependency must
    // not restart the pod, and neither must an unresponsive one.
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy)
      .Because("a hung dependency probe is a dependency problem, not a reason to kill a process "
             + "that is otherwise running");
  }

  [Test]
  public async Task ATimedOutSourceIsReportedAsFaultedAndNamedAsync() {
    var options = new WhizbangHealthOptions { SourceTimeout = TimeSpan.FromMilliseconds(150) };
    var aggregator = new WhizbangHealthAggregator([new HangingSource("transport")], options);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, cts.Token);

    var report = result.Components.Single(c => c.Component == "transport");
    // Silently reporting Healthy would hide the very thing that needs looking at.
    await Assert.That(report.State).IsEqualTo(ComponentState.Faulted);
    await Assert.That(report.Detail).IsNotNull();
  }

  [Test]
  public async Task AHealthySourceIsUnaffectedByAHangingOneAsync() {
    var options = new WhizbangHealthOptions { SourceTimeout = TimeSpan.FromMilliseconds(150) };
    var aggregator = new WhizbangHealthAggregator(
      [new HangingSource("transport"), new FastSource("schema", ComponentState.Operational)], options);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, cts.Token);

    var schema = result.Components.Single(c => c.Component == "schema");
    await Assert.That(schema.State).IsEqualTo(ComponentState.Operational)
      .Because("one unresponsive source must not cost the diagnosis of every other component");
  }

  [Test]
  public async Task SeveralHangingSourcesDoNotAddUpAsync() {
    var options = new WhizbangHealthOptions { SourceTimeout = TimeSpan.FromMilliseconds(300) };
    var aggregator = new WhizbangHealthAggregator(
      [new HangingSource("a"), new HangingSource("b"), new HangingSource("c"),
       new HangingSource("d"), new HangingSource("e")], options);

    var started = DateTimeOffset.UtcNow;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    _ = await aggregator.EvaluateAsync(HealthProbe.Liveness, cts.Token);
    var elapsed = DateTimeOffset.UtcNow - started;

    // Sequentially this is five timeouts, and a probe with a two-second deadline still fails.
    // Evaluated together it is one. This asserts a generous bound, not a tight one: the point is
    // that the cost does not scale with the number of sources.
    await Assert.That(elapsed).IsLessThan(TimeSpan.FromMilliseconds(1200))
      .Because("a per-source timeout that accumulates still times the probe out once enough "
             + "sources are unresponsive");
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
