// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Branch coverage for <see cref="WhizbangHealthAggregator"/>'s source materialization: a lazily
/// enumerated sequence (not already a list) is materialized once at construction, so every
/// evaluation indexes the same sources and never re-runs the sequence.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Health/WhizbangHealthAggregator.cs</code-under-test>
public class WhizbangHealthAggregatorBranchCoverageTests {

  private sealed class FixedSource(string component, ComponentState state) : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) =>
      ValueTask.FromResult(new ComponentHealth(state));
  }

  private sealed class CountingSequence(params IWhizbangHealthSource[] sources) {
    public int Enumerations { get; private set; }

    public IEnumerable<IWhizbangHealthSource> Lazily() {
      Enumerations++;
      foreach (var source in sources) {
        yield return source;
      }
    }
  }

  [Test]
  public async Task EvaluateAsync_LazySequenceOfSources_IsMaterializedOnceAndEvaluatedEveryTimeAsync() {
    var sequence = new CountingSequence(
      new FixedSource("alpha", ComponentState.Ready),
      new FixedSource("beta", ComponentState.Faulted));
    var aggregator = new WhizbangHealthAggregator(sequence.Lazily(), new WhizbangHealthOptions());

    var first = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    var second = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(sequence.Enumerations).IsEqualTo(1)
      .Because("the sources are paired with their results by index, which a re-enumerated lazy sequence cannot "
        + "guarantee, so they are materialized exactly once");
    await Assert.That(first.Components.Select(c => c.Component).ToList()).IsEquivalentTo(["alpha", "beta"]);
    await Assert.That(second.Components.Select(c => c.Component).ToList()).IsEquivalentTo(["alpha", "beta"]);
    await Assert.That(second.Status).IsEqualTo(HealthStatus.Unhealthy)
      .Because("the faulted source is still evaluated on the second probe, so readiness still fails");
  }
}
