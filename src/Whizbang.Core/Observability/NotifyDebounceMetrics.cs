using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Whizbang.Core.Observability;

/// <summary>
/// OTel meters for the adaptive doorbell-debounce controller (migration 137). Uses ObservableGauge
/// with cached values refreshed by <see cref="NotifyDebounceStatsCollector"/> — the
/// <see cref="TableStatisticsMetrics"/> idiom. Every instrument is tagged by <c>payload_kind</c>
/// because the question these answer is comparative: one kind flooding (debouncing) while another
/// stays real-time. Meter name: <c>Whizbang.NotifyDebounce</c>.
/// </summary>
/// <remarks>
/// A concurrency controller nobody can see is one nobody can debug. These gauges make the regime
/// (<c>effective_window_ms</c>) and the flood depth (<c>rapid_run</c>) visible, and the
/// fired/suppressed volumes show how much redundant notify load the debounce is absorbing.
/// <c>effective_window_ms</c> at the floor means notify is real-time for that kind; at the ceiling
/// it is debouncing a genuine flood — the exact state that, while invisible, made an
/// interactive-latency regression hard to see.
/// </remarks>
/// <docs>operations/observability/metrics#notify-debounce</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/NotifyDebounceMetricsTests.cs</tests>
public sealed class NotifyDebounceMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.NotifyDebounce";
#pragma warning restore CA1707

  private readonly ConcurrentDictionary<string, NotifyDebounceKindStats> _byKind =
    new(StringComparer.Ordinal);

  /// <summary>Initializes the adaptive-debounce meters on the shared Whizbang meter.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public NotifyDebounceMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    meter.CreateObservableGauge(
      "whizbang.notify.doorbell_fired",
      observeValues: () => _byKind.Select(kv => new Measurement<long>(kv.Value.FiredCount, _tag(kv.Key))),
      unit: "doorbells",
      description: "Doorbells fired per payload kind, summed across live target rows (cumulative; resets as rows age out — use increase()/deriv() for a rate)");

    // The redundant pg_notify load the debounce exists to remove: a rising suppressed count against
    // a flat fired count is the debounce doing its job under a flood.
    meter.CreateObservableGauge(
      "whizbang.notify.doorbell_suppressed",
      observeValues: () => _byKind.Select(kv => new Measurement<long>(kv.Value.SuppressedCount, _tag(kv.Key))),
      unit: "doorbells",
      description: "Doorbells suppressed (debounced) per payload kind, summed across live rows — the redundant notify load the debounce absorbs");

    // The regime, directly: at the floor the doorbell is real-time; at the ceiling the controller
    // has decided this kind is flooding a draining target and is debouncing it.
    meter.CreateObservableGauge(
      "whizbang.notify.effective_window_ms",
      observeValues: () => _byKind.Select(kv => new Measurement<int>(kv.Value.MaxEffectiveWindowMs, _tag(kv.Key))),
      unit: "ms",
      description: "Largest current effective suppression window per payload kind — the floor means real-time delivery, the ceiling means an active flood (the regime)");

    meter.CreateObservableGauge(
      "whizbang.notify.rapid_run",
      observeValues: () => _byKind.Select(kv => new Measurement<int>(kv.Value.MaxRapidRun, _tag(kv.Key))),
      unit: "doorbells",
      description: "Deepest current rapid-run depth per payload kind — how sustained the doorbell flood toward a target is");
  }

  private static KeyValuePair<string, object?> _tag(string kind) => new("payload_kind", kind);

  /// <summary>Replaces the cached readings. Called by <see cref="NotifyDebounceStatsCollector"/>.</summary>
  /// <param name="stats">One reading per payload kind.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stats"/> is null.</exception>
  public void Update(IReadOnlyList<NotifyDebounceKindStats> stats) {
    ArgumentNullException.ThrowIfNull(stats);
    foreach (var s in stats) {
      _byKind[s.PayloadKind] = s;
    }
  }

  /// <summary>
  /// Test seam: reads back a published reading. Observable gauges are only sampled by an active
  /// meter listener, so asserting on the cache is the direct way to prove a value was fed.
  /// </summary>
  internal NotifyDebounceKindStats? GetForTest(string kind) =>
    _byKind.TryGetValue(kind, out var v) ? v : null;
}
