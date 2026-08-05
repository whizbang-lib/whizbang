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
  }
}
