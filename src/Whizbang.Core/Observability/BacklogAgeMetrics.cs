using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Whizbang.Core.Observability;

/// <summary>
/// OTel meters for the traffic-class observability surface (topology arc phase 10, spec increment
/// 5): per-class ops-rate gauges, and per-entity backlog depth + oldest-enqueue age. Refreshed by
/// <see cref="BacklogAgeWorker"/> and read through ObservableGauges, the
/// <see cref="TableStatisticsMetrics"/> idiom.
/// Meter name: <c>Whizbang.TrafficClasses</c>.
/// </summary>
/// <remarks>
/// Every instrument here carries <c>traffic_class</c> and <c>transport_namespace</c>, because the
/// question these metrics exist to answer is comparative: one class starving another, or one
/// namespace's quota exhausted while its neighbour idles. A single aggregate number for the whole
/// host was exactly what made the motivating incident invisible.
/// </remarks>
/// <docs>operations/observability/metrics#traffic-classes</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs:PeekOnce_PerClassAndPerNamespace_AreCarriedThroughToTheGaugesAsync</tests>
public sealed class BacklogAgeMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.TrafficClasses";
#pragma warning restore CA1707

  private readonly ConcurrentDictionary<string, BacklogGaugeSample> _backlogs = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, OpsRateGaugeSample> _opsRates = new(StringComparer.Ordinal);

  /// <summary>One entity's cached backlog reading.</summary>
  /// <param name="Transport">Short transport tag.</param>
  /// <param name="TransportNamespace">The broker namespace key.</param>
  /// <param name="TrafficClass">The traffic class the entity carries.</param>
  /// <param name="Depth">Messages waiting.</param>
  /// <param name="OldestAgeSeconds">Oldest message age in seconds, or null when unavailable.</param>
  public readonly record struct BacklogGaugeSample(
    string Transport, string TransportNamespace, string TrafficClass, long Depth, double? OldestAgeSeconds);

  /// <summary>One namespace's cached ops-rate projection.</summary>
  /// <param name="Transport">Short transport tag.</param>
  /// <param name="TransportNamespace">The broker namespace key.</param>
  /// <param name="TrafficClass">The traffic class routed to that namespace.</param>
  /// <param name="OpsPerSecond">Projected broker operations per second.</param>
  public readonly record struct OpsRateGaugeSample(
    string Transport, string TransportNamespace, string TrafficClass, double OpsPerSecond);

  /// <summary>Initializes the traffic-class meters on the shared Whizbang meter.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public BacklogAgeMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    meter.CreateObservableGauge(
      "whizbang.traffic_class.backlog_depth",
      observeValues: () => _backlogs.Select(kv => new Measurement<long>(
        kv.Value.Depth, _backlogTags(kv.Key, kv.Value))),
      unit: "messages",
      description: "Messages waiting per entity, tagged by transport, namespace and traffic class");

    // The signal depth cannot give: a deep-but-young backlog is a burst draining normally, while a
    // shallow-but-ancient one is a consumer that stopped. Only the second needs an operator.
    meter.CreateObservableGauge(
      "whizbang.traffic_class.backlog_age_seconds",
      observeValues: () => _backlogs
        .Where(kv => kv.Value.OldestAgeSeconds.HasValue)
        .Select(kv => new Measurement<double>(
          kv.Value.OldestAgeSeconds!.Value, _backlogTags(kv.Key, kv.Value))),
      unit: "s",
      description: "Age of the oldest waiting message per entity — the hostage-versus-poison discriminator");

    meter.CreateObservableGauge(
      "whizbang.traffic_class.ops_rate",
      observeValues: () => _opsRates.Select(kv => new Measurement<double>(
        kv.Value.OpsPerSecond,
        new KeyValuePair<string, object?>("transport", kv.Value.Transport),
        new KeyValuePair<string, object?>("transport_namespace", kv.Value.TransportNamespace),
        new KeyValuePair<string, object?>("traffic_class", kv.Value.TrafficClass))),
      unit: "{operation}/s",
      description: "Projected broker operations per second per traffic class — the idle-churn witness");
  }

  private static KeyValuePair<string, object?>[] _backlogTags(string entity, BacklogGaugeSample sample) => [
    new("entity", entity),
    new("transport", sample.Transport),
    new("transport_namespace", sample.TransportNamespace),
    new("traffic_class", sample.TrafficClass),
  ];

  /// <summary>Replaces the cached backlog readings with one whole tick's answer.</summary>
  /// <param name="samples">Readings keyed by entity name.</param>
  /// <exception cref="ArgumentNullException">Thrown when samples is null.</exception>
  public void UpdateBacklogs(IReadOnlyDictionary<string, BacklogGaugeSample> samples) {
    ArgumentNullException.ThrowIfNull(samples);
    foreach (var stale in _backlogs.Keys.Where(k => !samples.ContainsKey(k))) {
      _backlogs.TryRemove(stale, out _);
    }
    foreach (var (entity, sample) in samples) {
      _backlogs[entity] = sample;
    }
  }

  /// <summary>Replaces the cached ops-rate projections with one whole tick's answer.</summary>
  /// <param name="samples">Projections keyed by TransportNamespace key.</param>
  /// <exception cref="ArgumentNullException">Thrown when samples is null.</exception>
  public void UpdateOpsRates(IReadOnlyDictionary<string, OpsRateGaugeSample> samples) {
    ArgumentNullException.ThrowIfNull(samples);
    foreach (var stale in _opsRates.Keys.Where(k => !samples.ContainsKey(k))) {
      _opsRates.TryRemove(stale, out _);
    }
    foreach (var (namespaceKey, sample) in samples) {
      _opsRates[namespaceKey] = sample;
    }
  }

  /// <summary>
  /// Reads back one entity's cached backlog. Observable gauges are only sampled by an active meter
  /// listener, so asserting on the cache is the direct way to test the refresh path.
  /// </summary>
  /// <param name="entity">The entity name.</param>
  /// <returns>The cached sample, or null.</returns>
  internal BacklogGaugeSample? GetBacklogForTest(string entity) =>
    _backlogs.TryGetValue(entity, out var sample) ? sample : null;

  /// <summary>Reads back one namespace's cached ops-rate projection (test seam).</summary>
  /// <param name="namespaceKey">The TransportNamespace key.</param>
  /// <returns>The cached sample, or null.</returns>
  internal OpsRateGaugeSample? GetOpsRateForTest(string namespaceKey) =>
    _opsRates.TryGetValue(namespaceKey, out var sample) ? sample : null;
}
