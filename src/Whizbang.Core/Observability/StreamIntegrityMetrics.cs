using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the stream-integrity subsystem (checkpoints, gap detection, backfill, deep audit,
/// self-healing repair). Meter name: <c>Whizbang.StreamIntegrity</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stream integrity is SELF-HEALING by default (<see cref="Whizbang.Core.Messaging.IntegrityRepairMode.AutoRepairCapped"/>),
/// which makes this surface load-bearing: operators must be able to SEE what the healer detects
/// and repairs. The two alarm-worthy signals:
/// </para>
/// <list type="bullet">
///   <item><description><b>GapsDetected / DivergencesDetected sustained &gt; 0</b> — deliveries are
///   being lost and repaired; find the transport/infrastructure cause.</description></item>
///   <item><description><b>DigestDriftHealed &gt; 0</b> — the incrementally-maintained digest table
///   disagreed with the store recompute: an unaccounted write path touched audited rows.</description></item>
/// </list>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityMetricsTests.cs</tests>
public sealed class StreamIntegrityMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.StreamIntegrity";
#pragma warning restore CA1707

  /// <summary>Phase B checkpoints this origin published (empty windows included — the liveness beat).</summary>
  public Counter<long> CheckpointsPublished { get; }

  /// <summary>Phase B checkpoints received from other origins. Tagged by origin.</summary>
  public Counter<long> CheckpointsReceived { get; }

  /// <summary>CONFIRMED continuity gaps (deficit persisted past the next checkpoint). Tagged by origin + event_type.</summary>
  public Counter<long> GapsDetected { get; }

  /// <summary>CONFIRMED audit divergences (bucket digest disagreed with the origin's manifest). Tagged by origin + event_type.</summary>
  public Counter<long> DivergencesDetected { get; }

  /// <summary>LOCAL coverage gaps (settled history a registered perspective never folded). Tagged by perspective.</summary>
  public Counter<long> CoverageGapsDetected { get; }

  /// <summary>Scoped re-delivery repair requests sent. Tagged by source (checkpoint | audit) + origin.</summary>
  public Counter<long> RepairsRequested { get; }

  /// <summary>LOCAL rebuilds dispatched for coverage gaps. Tagged by perspective.</summary>
  public Counter<long> RebuildsRequested { get; }

  /// <summary>Manifest requests sent to origins by the audit worker. Tagged by origin + sweep.</summary>
  public Counter<long> ManifestsRequested { get; }

  /// <summary>Manifest chunks answered as an origin. Tagged by level.</summary>
  public Counter<long> ManifestChunksSent { get; }

  /// <summary>Type-level mismatches escalated to stream-level manifest requests. Tagged by origin.</summary>
  public Counter<long> DrillDownsRequested { get; }

  /// <summary>Phase S state-only backfill requests broadcast for consumed-set growth (value = new type count).</summary>
  public Counter<long> BackfillsRequested { get; }

  /// <summary>Settled digest buckets checked by the trust-but-verify sweep.</summary>
  public Counter<long> DigestBucketsVerified { get; }

  /// <summary>Digest buckets the sweep HEALED (drift — an unaccounted write path). Tagged by kind (updated | removed | added).</summary>
  public Counter<long> DigestDriftHealed { get; }

  /// <summary>Re-delivery requests served as an origin (repair + backfill flows).</summary>
  public Counter<long> RedeliveryRequestsReceived { get; }

  /// <summary>Origin side: seconds from receiving a manifest request to publishing its last chunk.
  /// Tagged level / windowed / recompute. Epoch-served answers should be milliseconds — a slow
  /// answer means the epochs are not serving and the fold fell back to the store.</summary>
  public Histogram<double> ManifestAnswerDuration { get; }

  /// <summary>Consumer side: seconds from receiving a manifest chunk to finishing its comparison
  /// (ledger batches and repair sends included). THE early warning for the compare-slower-than-
  /// arrival failure: when p99 approaches the manifest arrival interval, chunks queue behind the
  /// gate and every queued chunk holds its deserialized payload in memory.</summary>
  public Histogram<double> ManifestCompareDuration { get; }

  /// <summary>Origin side: seconds to select and publish one redelivery request's bundles.</summary>
  public Histogram<double> RedeliveryBuildDuration { get; }

  /// <summary>Seconds from a divergent bucket's FIRST sighting to its proven heal — the per-stream
  /// time-to-reconcile. Read back from the heal's own delete (the row already carried the clock);
  /// no extra work is done to measure it.</summary>
  public Histogram<double> BucketHealSeconds { get; }

  /// <summary>Manifest chunks declined at the non-queueing compare gate. Sustained growth means
  /// comparisons cannot keep up with arrivals — the pressure reading behind the gate.</summary>
  public Counter<long> ComparesDeclined { get; }

  /// <summary>Events selected and shipped in redelivery bundles as an origin.</summary>
  public Counter<long> RedeliveryEventsShipped { get; }

  /// <summary>Repair traffic discarded because RepairMode is ReportOnly: re-delivery requests declined as an
  /// origin, bundles completed without fan-out as a consumer, and parked rows swept by maintenance (tag
  /// <c>role</c>: origin_request, consumer_bundle, maintenance_sweep).</summary>
  public Counter<long> RepairTrafficDiscarded { get; }

  /// <summary>Windowed stream pages followed via the resume cursor (per follow).</summary>
  public Counter<long> ManifestPagesFollowed { get; }

  /// <summary>Cursor-follow chains stopped at MaxManifestPagesPerAudit. Persistent growth means
  /// lanes are wider than the page budget covers per audit — raise the cap or accept the pace.</summary>
  public Counter<long> ManifestPagesCapped { get; }

  /// <summary>Initializes a new instance of <see cref="StreamIntegrityMetrics"/>.</summary>
  public StreamIntegrityMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    CheckpointsPublished = meter.CreateCounter<long>(
      "whizbang.stream_integrity.checkpoints_published",
      description: "Continuity checkpoints published (empty windows included — the liveness beat)");
    CheckpointsReceived = meter.CreateCounter<long>(
      "whizbang.stream_integrity.checkpoints_received",
      description: "Continuity checkpoints received; tagged by origin");
    GapsDetected = meter.CreateCounter<long>(
      "whizbang.stream_integrity.gaps_detected",
      description: "CONFIRMED continuity gaps; tagged by origin + event_type — sustained non-zero means deliveries are being lost");
    DivergencesDetected = meter.CreateCounter<long>(
      "whizbang.stream_integrity.divergences_detected",
      description: "CONFIRMED audit divergences; tagged by origin + event_type");
    CoverageGapsDetected = meter.CreateCounter<long>(
      "whizbang.stream_integrity.coverage_gaps_detected",
      description: "LOCAL perspective coverage gaps; tagged by perspective");
    RepairsRequested = meter.CreateCounter<long>(
      "whizbang.stream_integrity.repairs_requested",
      description: "Scoped re-delivery repair requests sent; tagged by source (checkpoint | audit) + origin");
    RebuildsRequested = meter.CreateCounter<long>(
      "whizbang.stream_integrity.rebuilds_requested",
      description: "LOCAL rebuilds dispatched for coverage gaps; tagged by perspective");
    ManifestsRequested = meter.CreateCounter<long>(
      "whizbang.stream_integrity.manifests_requested",
      description: "Manifest requests sent to origins; tagged by origin + sweep");
    ManifestChunksSent = meter.CreateCounter<long>(
      "whizbang.stream_integrity.manifest_chunks_sent",
      description: "Manifest chunks answered as an origin; tagged by level");
    DrillDownsRequested = meter.CreateCounter<long>(
      "whizbang.stream_integrity.drill_downs_requested",
      description: "Type-level mismatches escalated to stream-level requests; tagged by origin");
    BackfillsRequested = meter.CreateCounter<long>(
      "whizbang.stream_integrity.backfills_requested",
      description: "State-only backfill requests broadcast for consumed-set growth (value = new type count)");
    DigestBucketsVerified = meter.CreateCounter<long>(
      "whizbang.stream_integrity.digest_buckets_verified",
      description: "Settled digest buckets checked by the trust-but-verify sweep");
    DigestDriftHealed = meter.CreateCounter<long>(
      "whizbang.stream_integrity.digest_drift_healed",
      description: "Digest buckets the sweep healed; tagged by kind (updated | removed | added) — non-zero means an unaccounted write path");
    RedeliveryRequestsReceived = meter.CreateCounter<long>(
      "whizbang.stream_integrity.redelivery_requests_received",
      description: "Re-delivery requests served as an origin (repair + backfill flows)");

    ManifestAnswerDuration = meter.CreateHistogram<double>(
      "whizbang.stream_integrity.manifest_answer_duration", unit: "s",
      description: "Origin: receipt of a manifest request to its last chunk published (epoch-served answers should be ms)");
    ManifestCompareDuration = meter.CreateHistogram<double>(
      "whizbang.stream_integrity.manifest_compare_duration", unit: "s",
      description: "Consumer: receipt of a manifest chunk to comparison done — p99 nearing the arrival interval means chunks are queuing");
    RedeliveryBuildDuration = meter.CreateHistogram<double>(
      "whizbang.stream_integrity.redelivery_build_duration", unit: "s",
      description: "Origin: one redelivery request's select-and-publish, end to end");
    BucketHealSeconds = meter.CreateHistogram<double>(
      "whizbang.stream_integrity.bucket_heal_seconds", unit: "s",
      description: "First sighting of a divergent bucket to its proven heal — per-stream time-to-reconcile");
    ComparesDeclined = meter.CreateCounter<long>(
      "whizbang.stream_integrity.compares_declined",
      description: "Manifest chunks declined at the busy compare gate; sustained growth = comparisons losing to arrivals");
    RedeliveryEventsShipped = meter.CreateCounter<long>(
      "whizbang.stream_integrity.redelivery_events_shipped",
      description: "Events selected and shipped in redelivery bundles as an origin");
    ManifestPagesFollowed = meter.CreateCounter<long>(
      "whizbang.stream_integrity.manifest_pages_followed",
      description: "Windowed stream pages followed via the resume cursor");
    ManifestPagesCapped = meter.CreateCounter<long>(
      "whizbang.stream_integrity.manifest_pages_capped",
      description: "Cursor-follow chains stopped at the per-window page budget");
    RepairTrafficDiscarded = meter.CreateCounter<long>(
      "whizbang.stream_integrity.repair_traffic_discarded",
      description: "Repair requests, bundles and parked repair rows discarded because RepairMode is ReportOnly (tag role)");

    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.sealed_through",
      () => {
        var seals = _ledger.Seals;
        var measurements = new Measurement<long>[seals.Count];
        for (var i = 0; i < seals.Count; i++) {
          measurements[i] = new Measurement<long>(seals[i].SealedThrough,
            new KeyValuePair<string, object?>("origin_service_id", seals[i].OriginServiceId.ToString()));
        }
        return measurements;
      },
      description: "Per-origin verified watermark — the exclusive end of the highest window that audited clean and complete");

    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.unhealed_buckets",
      () => _ledger.UnhealedBuckets,
      description: "Divergent buckets currently unhealed (ledger rows). Falls as repair works — a heal deletes the row");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.repair_exhausted_buckets",
      () => _ledger.RepairExhausted,
      description: "Unhealed buckets that have spent their repair budget and stopped asking — these need operator attention, not patience");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.oldest_unhealed_age_seconds",
      () => _ledger.OldestUnhealedAgeSeconds,
      description: "Age of the longest-standing unhealed divergence; distinguishes a transient blip from a stuck one");
  }

  private volatile LedgerGaugeSnapshot _ledger = LedgerGaugeSnapshot.Empty;

  /// <summary>
  /// Publishes the ledger's current state to the gauges above.
  /// </summary>
  /// <remarks>
  /// These gauges are what an operator watches instead of a stream of report events. A counter
  /// ("how many divergences have we ever noticed") only ever rises and says nothing about now;
  /// these fall on their own as buckets heal, because healing deletes the row. Written from a
  /// collector on an interval and read by the meter's observation callback, so the snapshot is
  /// swapped atomically rather than mutated field by field — a half-updated reading would be
  /// indistinguishable from a real one.
  /// </remarks>
  public void UpdateLedgerGauges(LedgerGaugeSnapshot snapshot) =>
    _ledger = snapshot ?? LedgerGaugeSnapshot.Empty;

  /// <summary>The reading the gauges would currently report — the observation callbacks' source.</summary>
  internal LedgerGaugeSnapshot CurrentLedgerGaugesForTest => _ledger;
}

/// <summary>An atomically-swappable reading of the integrity ledger, for the gauges.</summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record LedgerGaugeSnapshot {
  /// <summary>Nothing diverged (also the pre-first-collection reading).</summary>
  public static LedgerGaugeSnapshot Empty { get; } = new();

  /// <summary>Divergent buckets currently unhealed.</summary>
  public long UnhealedBuckets { get; init; }

  /// <summary>Unhealed buckets past their repair-attempt budget.</summary>
  public long RepairExhausted { get; init; }

  /// <summary>Seconds since the oldest unhealed bucket first diverged (0 when none).</summary>
  public double OldestUnhealedAgeSeconds { get; init; }

  /// <summary>Per-origin verified watermarks (one gauge series each). Empty until the collector
  /// reads the seals — a tiny table it visits in the same breath as the ledger summary.</summary>
  public IReadOnlyList<OriginSeal> Seals { get; init; } = [];
}

/// <summary>One origin's verified watermark, for the <c>sealed_through</c> gauge.</summary>
/// <param name="OriginServiceId">The audited origin.</param>
/// <param name="SealedThrough">Exclusive end of the highest window that audited clean and complete.</param>
/// <docs>resilience/stream-integrity</docs>
public sealed record OriginSeal(Guid OriginServiceId, long SealedThrough);
