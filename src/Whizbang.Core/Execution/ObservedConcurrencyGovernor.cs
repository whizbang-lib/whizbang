using System;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Execution;

/// <summary>
/// Wraps any <see cref="IConcurrencyGovernor"/> so its decisions and the evidence behind them
/// reach OpenTelemetry.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than instrumentation inside each strategy. The governor is an interface, so
/// per-implementation telemetry would mean every future strategy re-implements the same emit code
/// — and the one that forgets becomes invisible exactly when someone is trying to work out why
/// throughput moved. Instrumenting the seam covers every implementation, including ones not
/// written yet.
/// </para>
/// <para>
/// It exports the decision AND its inputs, because the decision alone is not diagnosable. Seeing
/// width drop from 30 to 22 tells you nothing; seeing it drop while completed-per-second fell and
/// queue depth stayed high tells you the governor read contention — and seeing it drop while the
/// queue was empty tells you the governor is wrong.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Execution/ObservedConcurrencyGovernorTests.cs</tests>
public sealed class ObservedConcurrencyGovernor : IConcurrencyGovernor {
  private readonly IConcurrencyGovernor _inner;
  private readonly GovernorMetrics _metrics;
  private readonly string _name;

  /// <summary>Wraps <paramref name="inner"/>, exporting under <paramref name="name"/>.</summary>
  /// <param name="name">Stable series name, e.g. <c>outbox-drain</c>.</param>
  /// <param name="inner">The governor whose decisions are exported.</param>
  /// <param name="metrics">Meter holder.</param>
  public ObservedConcurrencyGovernor(string name, IConcurrencyGovernor inner, GovernorMetrics metrics) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(metrics);
    _name = name;
    _inner = inner;
    _metrics = metrics;
    _metrics.Track(name, inner);
  }

  /// <inheritdoc />
  public int CurrentWidth => _inner.CurrentWidth;

  /// <inheritdoc />
  public int Floor => _inner.Floor;

  /// <inheritdoc />
  public int Ceiling => _inner.Ceiling;

  /// <inheritdoc />
  public void Observe(GovernorSignal signal) {
    // Width is read before AND after so the adjustment can be attributed to the observation that
    // caused it. Recording only the new width would leave the direction to be inferred from a
    // previous export, which is unreliable at any sane scrape interval.
    var before = _inner.CurrentWidth;
    _inner.Observe(signal);
    var after = _inner.CurrentWidth;

    _metrics.RecordObservation(_name, signal);
    _metrics.RecordAdjustment(_name, before, after);
  }
}
