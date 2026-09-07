using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Coverage-round-23 targets for <see cref="WhizbangHealthAggregator"/>'s per-source exception
/// handling. A health aggregator decides what a probe reports — if a single misbehaving source's
/// exception escaped <c>_reportBoundedAsync</c> instead of being turned into a Faulted report, the
/// <see cref="Task.WhenAll"/> in <c>EvaluateAsync</c> would fault the WHOLE evaluation, turning one
/// noisy dependency into a failed probe for every other, healthy component too.
/// </summary>
public class WhizbangHealthAggregatorCoverageTests {
  private sealed class _throwingSource(string component, Exception toThrow) : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) => throw toThrow;
  }

  [Test]
  public async Task EvaluateAsync_SourceThrowsOperationCanceledException_NotFromTheCallerToken_ReportsFaultedAsync() {
    var source = new _throwingSource("flaky", new OperationCanceledException("source's own internal cancellation"));
    var aggregator = new WhizbangHealthAggregator([source], new WhizbangHealthOptions());

    // CancellationToken.None is never canceled, so the source's OperationCanceledException cannot be
    // attributed to the caller's token — it must be reported as a fault, not silently swallowed or
    // left to propagate and fault the whole evaluation.
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Components.Count).IsEqualTo(1);
    await Assert.That(result.Components[0].State).IsEqualTo(ComponentState.Faulted)
      .Because("a source-originated cancellation unrelated to the caller's token is still a fault, not a "
        + "healthy/intentional state");
    await Assert.That(result.Components[0].Detail).IsEqualTo("health source was canceled");
  }

  [Test]
  public async Task EvaluateAsync_SourceThrowsUnexpectedException_ReportsFaultedWithExceptionTypeNameAsync() {
    var source = new _throwingSource("flaky", new InvalidOperationException("boom"));
    var aggregator = new WhizbangHealthAggregator([source], new WhizbangHealthOptions());

    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy)
      .Because("a throwing source is a faulted component, not a failed probe — but a fault still fails "
        + "readiness under the default policy");
    await Assert.That(result.Components[0].State).IsEqualTo(ComponentState.Faulted);
    await Assert.That(result.Components[0].Detail).IsEqualTo(nameof(InvalidOperationException))
      .Because("the exception's type name is the diagnostic detail an operator sees for the faulted "
        + "component — losing it here means every unexpected failure looks identical in the health report");
  }
}
