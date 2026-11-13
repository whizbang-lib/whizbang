// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
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

  /// <summary>The tag naming how a ledger bucket opened or closed (#2256).</summary>
  public const string HOW_TAG = "how";

  /// <summary><c>how</c> on <see cref="LedgerBucketsOpened"/>: a row recorded for a bucket the ledger did not hold.</summary>
  public const string OPENED_NEW = "new";

  /// <summary><c>how</c> on <see cref="LedgerBucketsOpened"/>: an unhealable mark lifted, so repair can act again.</summary>
  public const string OPENED_REOPENED = "reopened";

  /// <summary><c>how</c> on <see cref="LedgerBucketsClosed"/>: a comparison removed the row.</summary>
  public const string CLOSED_HEALED = "healed";

  /// <summary><c>how</c> on <see cref="LedgerBucketsClosed"/>: the row was removed without a heal.</summary>
  public const string CLOSED_FORGOTTEN = "forgotten";

  /// <summary><c>how</c> on <see cref="LedgerBucketsClosed"/>: the bucket was marked unhealable.</summary>
  public const string CLOSED_UNHEALABLE = "unhealable";
#pragma warning restore CA1707

  /// <summary>Phase B checkpoints this origin published (empty windows included — the liveness beat).</summary>
  public PassiveCounter<long> CheckpointsPublished { get; }

  /// <summary>Phase B checkpoints received from other origins. Tagged by origin.</summary>
  public PassiveCounter<long> CheckpointsReceived { get; }

  /// <summary>CONFIRMED continuity gaps (deficit persisted past the next checkpoint). Tagged by origin + event_type.</summary>
  public PassiveCounter<long> GapsDetected { get; }

  /// <summary>CONFIRMED audit divergences (bucket digest disagreed with the origin's manifest). Tagged by origin + event_type.</summary>
  public PassiveCounter<long> DivergencesDetected { get; }

  /// <summary>LOCAL coverage gaps (settled history a registered perspective never folded). Tagged by perspective.</summary>
  public PassiveCounter<long> CoverageGapsDetected { get; }

  /// <summary>Scoped re-delivery repair requests sent. Tagged by source (checkpoint | audit) + origin.</summary>
  public PassiveCounter<long> RepairsRequested { get; }

  /// <summary>LOCAL rebuilds dispatched for coverage gaps. Tagged by perspective.</summary>
  public PassiveCounter<long> RebuildsRequested { get; }

  /// <summary>Manifest requests sent to origins by the audit worker. Tagged by origin + sweep.</summary>
  public PassiveCounter<long> ManifestsRequested { get; }

  /// <summary>Manifest chunks answered as an origin. Tagged by level.</summary>
  public PassiveCounter<long> ManifestChunksSent { get; }

  /// <summary>Type-level mismatches escalated to stream-level manifest requests. Tagged by origin.</summary>
  public PassiveCounter<long> DrillDownsRequested { get; }

  /// <summary>
  /// #2102: windowed stream buckets a comparison could not decide because the consumer holds some of their events
  /// without the origin's commit sequence. Not deficits: none is recorded or repaired, and the full-history comparison
  /// decides those streams. Tagged by origin.
  /// </summary>
  /// <docs>resilience/stream-integrity#events-held-without-an-origin-sequence</docs>
  public PassiveCounter<long> UnplacedBuckets { get; }

  /// <summary>
  /// #2115: ledger rows the idle re-check claimed and asked their owner to answer again. Tagged by origin.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  public PassiveCounter<long> LedgerRowsRechecked { get; }

  /// <summary>
  /// #2115: ledger rows a re-check answer removed, because their bucket now matches or is undecided. Tagged by origin.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  public PassiveCounter<long> LedgerRowsRecheckCleared { get; }

  /// <summary>
  /// #2106: stream digest buckets the id-lane backfill found disagreeing with their events and corrected, each also
  /// logged at Warning with its stored and recomputed values. Sustained non-zero means a write path changes digests
  /// without the shared fold. Tagged by kind (drift_updated | drift_removed | drift_added).
  /// </summary>
  /// <docs>resilience/stream-integrity#one-digest-fold</docs>
  public PassiveCounter<long> StreamDigestDriftCorrected { get; }

  /// <summary>Phase S state-only backfill requests broadcast for consumed-set growth (value = new type count).</summary>
  public PassiveCounter<long> BackfillsRequested { get; }

  /// <summary>#2092: paced backfill windows requested of owners, retries included. Tagged by event_type.</summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  public PassiveCounter<long> BackfillWindowsRequested { get; }

  /// <summary>#2092: events the answered backfill windows selected. Tagged by event_type.</summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  public PassiveCounter<long> BackfillEventsReceived { get; }

  /// <summary>#2092: paced backfills that ended. Tagged by outcome (complete | unanswered).</summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  public PassiveCounter<long> BackfillsEnded { get; }

  /// <summary>
  /// #2098: redelivery answers an origin stopped at its event bound with more of the selection left. A windowed
  /// backfill continues from where the answer stopped; any other request does not, so a non-zero rate on the
  /// "ordinary" kind means requests wider than the bound.
  /// Tagged by kind (window | ordinary).
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  public PassiveCounter<long> RedeliveryAnswersCapped { get; }

  /// <summary>Settled digest buckets checked by the trust-but-verify sweep.</summary>
  public PassiveCounter<long> DigestBucketsVerified { get; }

  /// <summary>Digest buckets the sweep HEALED (drift — an unaccounted write path). Tagged by kind (updated | removed | added).</summary>
  public PassiveCounter<long> DigestDriftHealed { get; }

  /// <summary>Re-delivery requests served as an origin (repair + backfill flows).</summary>
  public PassiveCounter<long> RedeliveryRequestsReceived { get; }

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
  public PassiveCounter<long> ComparesDeclined { get; }

  /// <summary>Events selected and shipped in redelivery bundles as an origin.</summary>
  public PassiveCounter<long> RedeliveryEventsShipped { get; }

  /// <summary>Repair traffic discarded because RepairMode is ReportOnly: re-delivery requests declined as an
  /// origin, bundles completed without fan-out as a consumer, and parked rows swept by maintenance (tag
  /// <c>role</c>: origin_request, consumer_bundle, maintenance_sweep).</summary>
  public PassiveCounter<long> RepairTrafficDiscarded { get; }

  /// <summary>Windowed stream pages followed via the resume cursor (per follow).</summary>
  public PassiveCounter<long> ManifestPagesFollowed { get; }

  /// <summary>Cursor-follow chains stopped at MaxManifestPagesPerAudit. Persistent growth means
  /// lanes are wider than the page budget covers per audit — raise the cap or accept the pace.</summary>
  public PassiveCounter<long> ManifestPagesCapped { get; }

  /// <summary>Stored events whose empty origin columns a redelivery filled: history received before the store
  /// recorded origins, now verifiable under its origin. Growth during convergence after an upgrade is expected; it
  /// stops once that history has been redelivered once.</summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history</docs>
  public PassiveCounter<long> OriginsAdopted { get; }

  /// <summary>Stored events without an origin that a redelivery named an origin for but did not adopt, because the
  /// event's first hop names another service: an event this service authored, or one received from a third service,
  /// that the origin's store had filed as its own. Growth means an origin is redelivering events it did not author;
  /// the warning logged once per bundle names the origin.</summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history</docs>
  public PassiveCounter<long> OriginsRefused { get; }

  /// <summary>Buckets recorded as ones repair cannot change: a redelivery answered with events that were all already
  /// stored under their origin, so repair stops asking for them. Each bucket is counted once.</summary>
  /// <docs>resilience/stream-integrity#unhealable-buckets</docs>
  public PassiveCounter<long> RepairsUnhealable { get; }

  /// <summary>
  /// #1792: deficit buckets a manifest comparison could not add to the paced repair drain's ledger,
  /// deferred to a later audit. Tagged by origin and reason: <c>per_audit_cap</c>
  /// (<c>MaxAutoRepairRequestsPerAudit</c> new buckets already admitted by this comparison) or
  /// <c>backlog_cap</c> (the ledger holds <c>MaxRepairDrainBacklog</c> rows). Sustained growth means
  /// discovery outpaces the drain: raise <c>RepairDrainRatePerSecond</c> or accept the pace.
  /// </summary>
  public PassiveCounter<long> RepairsDeferred { get; }

  /// <summary>
  /// #2184: each time the paced repair drain starts holding because <c>MaxOutstandingRepairAnswers</c> buckets await
  /// their answer; counted once per hold, not per tick. A rising count means answers lag the requests: the drain is
  /// pacing itself to the rate they land at.
  /// </summary>
  /// <docs>resilience/stream-integrity#outstanding-repair-answers-are-capped</docs>
  public PassiveCounter<long> RepairDrainHolds { get; }

  /// <summary>Integrity deliveries recorded for the maintainer's drain (#2001); a copy already recorded is not counted
  /// again. Tagged by type.</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public PassiveCounter<long> IntegrityDeliveriesQueued { get; }

  /// <summary>Integrity deliveries the maintainer's drain handled. Tagged by type.</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public PassiveCounter<long> IntegrityDeliveriesProcessed { get; }

  /// <summary>Failed attempts at an integrity delivery, each retried after a backoff. Tagged by type.</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public PassiveCounter<long> IntegrityDeliveriesFailed { get; }

  /// <summary>Integrity deliveries dropped: attempts spent, or no handler registered for the type. Tagged by type and
  /// reason (<c>attempts_exhausted</c> | <c>no_handler</c>). Each drop is also logged as a warning naming the type.</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public PassiveCounter<long> IntegrityDeliveriesDropped { get; }

  /// <summary>Domains two services claim on their checkpoints, counted once per contesting pair and domain; neither
  /// claimant is used for the domain. Tagged by domain.</summary>
  /// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
  public PassiveCounter<long> OwnershipConflicts { get; }

  /// <summary>Redelivery requests an origin declined to answer. Tagged by reason (<c>own_request</c>: the service's
  /// own backfill broadcast came back to it).</summary>
  /// <docs>resilience/stream-integrity#redelivery-ships-only-what-the-origin-wrote</docs>
  public PassiveCounter<long> RedeliveryRequestsIgnored { get; }

  /// <summary>#2116: children of a redelivery bundle stored under their type's true owner, because the origin the bundle
  /// names does not own the type (it answered with events it had only received). Each bundle is also logged at Warning.</summary>
  /// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
  public PassiveCounter<long> RedeliveredChildrenReattributed { get; }

  /// <summary>
  /// #2273: repaired events (children of a redelivery bundle) by how their receptors were decided: tag <c>delivery</c>
  /// is <c>state_only</c> or <c>receptors</c> (the event runs the receptors declared to run on repair), tag
  /// <c>reason</c> says why (<see cref="Messaging.RepairReceptorReason"/> in snake case). Counted when the bundle is
  /// expanded, so a child the inbox then drops as a duplicate is counted too.
  /// </summary>
  /// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
  public PassiveCounter<long> RepairedEvents { get; }

  /// <summary>#2116: stored events recorded under an origin that does not own their type, re-attributed to the owner by
  /// the misattributed-origin correction. Each batch is also logged at Warning, per type, origin and owner.</summary>
  /// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
  public PassiveCounter<long> MisattributedOriginsCorrected { get; }

  /// <summary>Stored events received before origins were recorded that re-attribution gave their owner as origin.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public PassiveCounter<long> OriginsReattributed { get; }

  /// <summary>Stored events of a type this service does not own, with no known owner; out of the local lane logically.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public PassiveCounter<long> OriginsUnattributed { get; }

  /// <summary>Stored events re-attribution left because their first hop names a service other than the owner.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public PassiveCounter<long> ReattributionHopDisagreements { get; }

  /// <summary>
  /// #2256: ledger buckets opened. Tagged by how: <c>new</c> (a row recorded for a bucket the ledger did not hold) or
  /// <c>reopened</c> (a bucket marked unhealable whose local side or deficit changed, so repair can act on it again).
  /// Completion over a window is <c>closed / opened</c>.
  /// </summary>
  /// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
  public PassiveCounter<long> LedgerBucketsOpened { get; }

  /// <summary>
  /// #2256: ledger buckets closed, each counted once. Tagged by how: <c>healed</c> (a comparison removed the row: it
  /// matches, or can no longer be confirmed), <c>forgotten</c> (removed without a heal: an unconsumed type, a type its
  /// origin does not own, the row cap, a purged stream) or <c>unhealable</c> (marked as a bucket repair cannot change; a
  /// later removal of that row is not counted again). ETA is <c>ledger_open_buckets / rate(closed)</c>.
  /// </summary>
  /// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
  public PassiveCounter<long> LedgerBucketsClosed { get; }

  private static readonly string[] _deferralReasons = ["per_audit_cap", "backlog_cap"];
  private static readonly string[] _repairSources = ["checkpoint", "audit", "bulk", "drain", "operator"];
  private static readonly string[] _driftKinds = ["updated", "removed", "added", "epoch-refolded"];
  private static readonly string[] _discardRoles = ["origin_request", "consumer_bundle", "maintenance_sweep"];

  /// <summary>Initializes a new instance of <see cref="StreamIntegrityMetrics"/>.</summary>
  public StreamIntegrityMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);

    CheckpointsPublished = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.checkpoints_published",
      description: "Continuity checkpoints published (empty windows included — the liveness beat)");
    CheckpointsReceived = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.checkpoints_received",
      description: "Continuity checkpoints received; tagged by origin");
    GapsDetected = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.gaps_detected",
      description: "CONFIRMED continuity gaps; tagged by origin + event_type — sustained non-zero means deliveries are being lost");
    DivergencesDetected = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.divergences_detected",
      description: "CONFIRMED audit divergences; tagged by origin + event_type");
    CoverageGapsDetected = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.coverage_gaps_detected",
      description: "LOCAL perspective coverage gaps; tagged by perspective");
    RepairsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repairs_requested",
      description: "Scoped re-delivery repair requests sent; tagged by source (checkpoint | audit) + origin");
    RebuildsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.rebuilds_requested",
      description: "LOCAL rebuilds dispatched for coverage gaps; tagged by perspective");
    ManifestsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.manifests_requested",
      description: "Manifest requests sent to origins; tagged by origin + sweep");
    ManifestChunksSent = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.manifest_chunks_sent",
      description: "Manifest chunks answered as an origin; tagged by level");
    DrillDownsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.drill_downs_requested",
      description: "Type-level mismatches escalated to stream-level requests; tagged by origin");
    UnplacedBuckets = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.unplaced_buckets",
      description: "Windowed stream buckets left undecided because events are held without the origin's sequence; tagged by origin");
    LedgerRowsRechecked = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.ledger_rows_rechecked",
      description: "Ledger rows the idle re-check asked their owner to answer again; tagged by origin");
    LedgerRowsRecheckCleared = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.ledger_rows_recheck_cleared",
      description: "Ledger rows a re-check answer removed because their bucket now matches or is undecided; tagged by origin");
    StreamDigestDriftCorrected = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.stream_digest_drift_corrected",
      description: "Stream digest buckets the id-lane backfill found drifted from their events and corrected; tagged by kind");
    BackfillsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.backfills_requested",
      description: "State-only backfill requests broadcast for consumed-set growth (value = new type count)");
    DigestBucketsVerified = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.digest_buckets_verified",
      description: "Settled digest buckets checked by the trust-but-verify sweep");
    DigestDriftHealed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.digest_drift_healed",
      description: "Digest buckets the sweep healed; tagged by kind (updated | removed | added) — non-zero means an unaccounted write path");
    RedeliveryRequestsReceived = meter.CreatePassiveCounter<long>(
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
    ComparesDeclined = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.compares_declined",
      description: "Manifest chunks declined at the busy compare gate; sustained growth = comparisons losing to arrivals");
    RedeliveryEventsShipped = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.redelivery_events_shipped",
      description: "Events selected and shipped in redelivery bundles as an origin");
    ManifestPagesFollowed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.manifest_pages_followed",
      description: "Windowed stream pages followed via the resume cursor");
    ManifestPagesCapped = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.manifest_pages_capped",
      description: "Cursor-follow chains stopped at the per-window page budget");
    OriginsAdopted = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.origins_adopted",
      description: "Stored events whose empty origin columns a redelivery filled (history received before origins were recorded)");
    OriginsRefused = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.origins_refused",
      description: "Stored events without an origin that a redelivery did not adopt because their first hop names another service");
    RepairsUnhealable = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repairs_unhealable",
      description: "Buckets a redelivery could not change; repair stops asking for them until they change or heal");
    RepairsDeferred = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repairs_deferred",
      description: "Deficit buckets a comparison deferred instead of adding to the paced repair drain's ledger; tagged by origin + reason (per_audit_cap | backlog_cap)");
    RepairDrainHolds = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repair_drain_holds",
      description: "Times the paced repair drain stopped claiming because MaxOutstandingRepairAnswers buckets awaited their answer; once per hold");
    RepairTrafficDiscarded = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repair_traffic_discarded",
      description: "Repair requests, bundles and parked repair rows discarded because RepairMode is ReportOnly (tag role)");

    IntegrityDeliveriesQueued = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.deliveries_queued",
      description: "Integrity deliveries recorded for the maintainer's drain; tagged by type");
    IntegrityDeliveriesProcessed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.deliveries_processed",
      description: "Integrity deliveries the maintainer's drain handled; tagged by type");
    IntegrityDeliveriesFailed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.deliveries_failed",
      description: "Failed attempts at an integrity delivery, retried after a backoff; tagged by type");
    IntegrityDeliveriesDropped = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.deliveries_dropped",
      description: "Integrity deliveries dropped (attempts spent, or no handler); tagged by type + reason");
    OwnershipConflicts = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.ownership_conflicts",
      description: "Domains two services claim, once per contesting pair and domain; neither claimant is used; tagged by domain");
    RedeliveryRequestsIgnored = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.redelivery_requests_ignored",
      description: "Redelivery requests an origin declined to answer; tagged by reason (own_request)");
    RepairedEvents = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.repaired_events",
      description: "Repaired events by how their receptors were decided; tagged by delivery (state_only | receptors) + reason");
    RedeliveredChildrenReattributed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.redelivered_children_reattributed",
      description: "Redelivery bundle children stored under their type's true owner because the bundle's origin does not own the type");
    MisattributedOriginsCorrected = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.misattributed_origins_corrected",
      description: "Stored events recorded under an origin that does not own their type, re-attributed to the owner");
    OriginsReattributed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.origins_reattributed",
      description: "Stored events received before origins were recorded that re-attribution gave their owner as origin");
    OriginsUnattributed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.origins_unattributed",
      description: "Stored events of a type this service does not own whose owner is not known");
    ReattributionHopDisagreements = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.reattribution_hop_disagreements",
      description: "Stored events re-attribution left because their first hop names a service other than the owner");

    LedgerBucketsOpened = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.ledger_buckets_opened",
      description: "Integrity ledger buckets opened; tagged by how (new | reopened)");
    LedgerBucketsClosed = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.ledger_buckets_closed",
      description: "Integrity ledger buckets closed, each once; tagged by how (healed | forgotten | unhealable)");

    BackfillWindowsRequested = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.backfill_windows_requested",
      description: "Paced backfill windows requested of owners, retries included; tagged by event_type");
    BackfillEventsReceived = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.backfill_events_received",
      description: "Events the answered paced backfill windows selected; tagged by event_type");
    BackfillsEnded = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.backfills_ended",
      description: "Paced backfills that ended; tagged by outcome (complete | unanswered)");
    RedeliveryAnswersCapped = meter.CreatePassiveCounter<long>(
      "whizbang.stream_integrity.redelivery_answers_capped",
      description: "Redelivery answers stopped at the origin's event bound with more selected; tagged by kind (window | ordinary)");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.backfill_windows_in_flight",
      () => _backfill.WindowsInFlight,
      description: "Paced backfill windows requested and awaiting their answer");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.backfill_remaining_events",
      () => {
        var remaining = _backfill.Remaining;
        var measurements = new Measurement<long>[remaining.Count];
        for (var i = 0; i < remaining.Count; i++) {
          measurements[i] = new Measurement<long>(remaining[i].Events,
            new KeyValuePair<string, object?>("event_type", remaining[i].EventType));
        }
        return measurements;
      },
      description: "Events each running paced backfill is estimated to have left, by event_type; falls to zero as it finishes");

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
      description: "Divergent buckets currently unhealed (ledger rows), every kind; unhealed_buckets_by_kind splits it. Falls as repair works: a heal deletes the row");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.unhealed_buckets_by_kind",
      () => _kindMeasurements(_ledger),
      description: "Unhealed buckets by kind (deficit | surplus | digest_mismatch | unknown), adding up to unhealed_buckets; only a deficit is one repair heals");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.repair_exhausted_buckets",
      () => _ledger.RepairExhausted,
      description: "Unhealed buckets that have spent their repair budget and stopped asking — these need operator attention, not patience");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.oldest_unhealed_age_seconds",
      () => _ledger.OldestUnhealedAgeSeconds,
      description: "Age of the longest-standing unhealed divergence; distinguishes a transient blip from a stuck one");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.repair_answer_pending_buckets",
      () => _ledger.AnswerPendingBuckets,
      description: "Buckets whose repair request is out and whose answer has not landed; they are not requested again meanwhile");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.repair_answered_buckets",
      () => _ledger.AnsweredBuckets,
      description: "Buckets a redelivery answered that wait for the next comparison to decide healed or still divergent");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.unhealed_deficit_events",
      () => _ledger.UnhealedDeficitEvents,
      description: "Missing events, summed, in the buckets repair can still act on (neither unhealable nor exhausted); the data left to heal");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.unhealable_buckets",
      () => _ledger.UnhealableBuckets,
      description: "Ledger rows marked unhealable, which repair cannot change; splits unhealed_buckets into repairable and unhealable");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.deliveries_pending",
      () => _ledger.IntegrityDeliveriesPending,
      description: "Integrity deliveries waiting for the maintainer's drain; one value per service");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.oldest_delivery_age_seconds",
      () => _ledger.OldestIntegrityDeliveryAgeSeconds,
      description: "How long the oldest integrity delivery has waited for the maintainer's drain");

    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.ledger_open_buckets",
      () => _backlogMeasurements(_backlog),
      description: "Open integrity ledger buckets by origin and state (waiting | answered | unhealable | exhausted | open); reported by the maintainer duty's holder only");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.outbound_deliveries",
      () => _outboundMeasurements(_backlog),
      description: "Integrity deliveries waiting in the queue the maintainer drains; reported by the maintainer duty's holder only");

    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.verified_percent",
      () => _verifiedMeasurements(_verified),
      unit: "%",
      description: "Derived (#2261): each origin's verified watermark as a percentage of the latest commit sequence its checkpoints announced, by origin_service_id");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.verified_percent_all_origins",
      () => _optional(_verified.AllOrigins),
      unit: "%",
      description: "Derived (#2261): verified_percent over all origins, weighted by the commit sequences each announced");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.repair_progress_percent",
      () => _optional(_progress.ProgressPercent),
      unit: "%",
      description: "Derived (#2261): ledger buckets closed as a percentage of buckets opened over the rolling window, per service; reported by the maintainer duty's holder only");
    _ = meter.CreateObservableGauge(
      "whizbang.stream_integrity.backlog_eta_seconds",
      () => _optional(_progress.EtaSeconds),
      unit: "s",
      description: "Derived (#2261): repairable open buckets over the window's net close rate; no measurement while the backlog is flat or growing; reported by the maintainer duty's holder only");

    // Issue #711: closed tag domains exist at zero from construction (see PassiveCounter).
    LedgerBucketsOpened.Touch(HOW_TAG, _openedHows);
    LedgerBucketsClosed.Touch(HOW_TAG, _closedHows);
    RepairsRequested.Touch("source", _repairSources);
    ManifestChunksSent.Touch("level", Enum.GetNames<Whizbang.Core.Messaging.ManifestLevel>());
    DigestDriftHealed.Touch("kind", _driftKinds);
    RepairTrafficDiscarded.Touch("role", _discardRoles);
    RepairsDeferred.Touch("reason", _deferralReasons);
    BackfillsEnded.Touch("outcome", _backfillOutcomes);
    RedeliveryAnswersCapped.Touch("kind", _cappedKinds);
    foreach (var reason in Enum.GetValues<Whizbang.Core.Messaging.RepairReceptorReason>()) {
      RepairedEvents.Touch(_repairedTags(reason));
    }
  }

  // #2260: one measurement per kind, each always present (zero when none), so a healed kind falls to zero.
  private static Measurement<long>[] _kindMeasurements(LedgerGaugeSnapshot reading) => [
    new(reading.DeficitBuckets, new KeyValuePair<string, object?>(LedgerBucketKinds.TAG, LedgerBucketKinds.DEFICIT)),
    new(reading.SurplusBuckets, new KeyValuePair<string, object?>(LedgerBucketKinds.TAG, LedgerBucketKinds.SURPLUS)),
    new(reading.DigestMismatchBuckets, new KeyValuePair<string, object?>(LedgerBucketKinds.TAG, LedgerBucketKinds.DIGEST_MISMATCH)),
    new(reading.UnknownKindBuckets, new KeyValuePair<string, object?>(LedgerBucketKinds.TAG, LedgerBucketKinds.UNKNOWN)),
  ];

  // #2261: one series per origin with a known latest; none when the reading is withdrawn.
  private static Measurement<double>[] _verifiedMeasurements(IntegrityVerifiedReading reading) {
    var measurements = new Measurement<double>[reading.Origins.Count];
    for (var i = 0; i < reading.Origins.Count; i++) {
      measurements[i] = new Measurement<double>(reading.Origins[i].Percent,
        new KeyValuePair<string, object?>("origin_service_id", reading.Origins[i].OriginServiceId.ToString()));
    }
    return measurements;
  }

  // #2261: a derived value with no value reports no measurement, never a sentinel.
  private static Measurement<double>[] _optional(double? value) => value is { } v ? [new Measurement<double>(v)] : [];

  private static readonly string[] _openedHows = [OPENED_NEW, OPENED_REOPENED];
  private static readonly string[] _closedHows = [CLOSED_HEALED, CLOSED_FORGOTTEN, CLOSED_UNHEALABLE];

  // One measurement per origin and state; none at all when this instance does not report the backlog (#2256).
  private static Measurement<long>[] _backlogMeasurements(LedgerBacklogSnapshot reading) {
    if (!reading.Reported) {
      return [];
    }
    var measurements = new Measurement<long>[reading.Buckets.Count];
    for (var i = 0; i < reading.Buckets.Count; i++) {
      var count = reading.Buckets[i];
      measurements[i] = new Measurement<long>(count.Buckets,
        new KeyValuePair<string, object?>("origin", count.Origin),
        new KeyValuePair<string, object?>("state", count.State));
    }
    return measurements;
  }

  private static Measurement<long>[] _outboundMeasurements(LedgerBacklogSnapshot reading) =>
    reading.Reported ? [new Measurement<long>(reading.OutboundDeliveries)] : [];

  /// <summary>Counts <paramref name="count"/> repaired events decided for <paramref name="reason"/> (#2273).</summary>
  /// <param name="reason">Why they were delivered as they were.</param>
  /// <param name="count">How many.</param>
  /// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityMetricsRepairedEventsTests.cs</tests>
  public void RecordRepairedEvents(Whizbang.Core.Messaging.RepairReceptorReason reason, long count) =>
    RepairedEvents.Add(count, _repairedTags(reason));

  // One delivery per reason: only a child the ledger proves never handled runs receptors.
  private static TagList _repairedTags(Whizbang.Core.Messaging.RepairReceptorReason reason) => new() {
    { "delivery", reason == Whizbang.Core.Messaging.RepairReceptorReason.NeverHandled ? "receptors" : "state_only" },
    { "reason", _repairReasonNames[(int)reason] },
  };

  private static readonly string[] _repairReasonNames = [
    "requested_state_only", "no_lookback", "no_origin_time", "outside_lookback", "before_ledger_coverage",
    "ledger_unavailable", "already_handled", "never_handled",
  ];

  private static readonly string[] _backfillOutcomes = ["complete", "unanswered"];
  private static readonly string[] _cappedKinds = ["window", "ordinary"];

  private volatile BackfillGaugeSnapshot _backfill = BackfillGaugeSnapshot.Empty;

  /// <summary>Publishes the paced backfill's current state to its gauges (#2092), swapped atomically like the ledger's.</summary>
  /// <param name="snapshot">The reading; null resets to empty.</param>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SubscriptionBackfillWorkerTests.cs</tests>
  public void UpdateBackfillGauges(BackfillGaugeSnapshot snapshot) =>
    _backfill = snapshot ?? BackfillGaugeSnapshot.Empty;

  /// <summary>The backfill reading the gauges would currently report.</summary>
  internal BackfillGaugeSnapshot CurrentBackfillGaugesForTest => _backfill;

  private volatile LedgerBacklogSnapshot _backlog = LedgerBacklogSnapshot.NotReported;

  /// <summary>
  /// Publishes the open repair backlog to its gauges (#2256), or withdraws it: the not-reported reading (or null) makes
  /// both gauges report no series at all, which is what an instance that does not hold the maintainer duty reports.
  /// </summary>
  /// <param name="snapshot">The reading; null withdraws it.</param>
  /// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityBacklogMetricsTests.cs</tests>
  public void UpdateLedgerBacklog(LedgerBacklogSnapshot? snapshot) =>
    _backlog = snapshot ?? LedgerBacklogSnapshot.NotReported;

  /// <summary>The backlog reading the gauges would currently report.</summary>
  internal LedgerBacklogSnapshot CurrentLedgerBacklogForTest => _backlog;

  /// <summary>
  /// Records buckets newly marked unhealable (each once): <see cref="RepairsUnhealable"/>, and the same buckets as
  /// closed (<c>how=unhealable</c>) on <see cref="LedgerBucketsClosed"/>, so the two cannot disagree (#2256).
  /// </summary>
  /// <param name="buckets">Buckets the mark changed.</param>
  /// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityBacklogMetricsTests.cs</tests>
  public void RecordBucketsMarkedUnhealable(long buckets) {
    if (buckets <= 0) {
      return;
    }
    RepairsUnhealable.Add(buckets);
    LedgerBucketsClosed.Add(buckets, new KeyValuePair<string, object?>(HOW_TAG, CLOSED_UNHEALABLE));
  }

  private volatile IntegrityVerifiedReading _verified = IntegrityVerifiedReading.NotReported;

  /// <summary>
  /// Publishes the <c>verified_percent</c> gauges' reading (#2261), or withdraws it (null): both gauges then report no
  /// series.
  /// </summary>
  /// <param name="reading">The reading; null withdraws it.</param>
  /// <docs>resilience/stream-integrity#derived-completion-meters</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityCompletionMetricsTests.cs</tests>
  public void UpdateVerifiedPercent(IntegrityVerifiedReading? reading) =>
    _verified = reading ?? IntegrityVerifiedReading.NotReported;

  /// <summary>The verified reading the gauges would currently report.</summary>
  internal IntegrityVerifiedReading CurrentVerifiedPercentForTest => _verified;

  private volatile RepairProgressReading _progress = RepairProgressReading.NotReported;

  /// <summary>
  /// Publishes the <c>repair_progress_percent</c> and <c>backlog_eta_seconds</c> reading (#2261), or withdraws it (null):
  /// both gauges then report no series, which is what an instance that does not report the backlog reports.
  /// </summary>
  /// <param name="reading">The reading; null withdraws it.</param>
  /// <docs>resilience/stream-integrity#derived-completion-meters</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StreamIntegrityCompletionMetricsTests.cs</tests>
  public void UpdateRepairProgress(RepairProgressReading? reading) =>
    _progress = reading ?? RepairProgressReading.NotReported;

  /// <summary>The repair progress reading the gauges would currently report.</summary>
  internal RepairProgressReading CurrentRepairProgressForTest => _progress;

  private long _talliedOpened;
  private long _talliedClosed;

  /// <summary>
  /// #2261: the buckets the opened and closed counters counted that the shared tally has not been told of yet. The
  /// counters are the source, whoever adds to them, so the tally and the counters cannot disagree.
  /// </summary>
  internal (long Opened, long Closed) PendingLedgerTally() =>
    (LedgerBucketsOpened.Total - Interlocked.Read(ref _talliedOpened), LedgerBucketsClosed.Total - Interlocked.Read(ref _talliedClosed));

  /// <summary>#2261: records that the shared tally now holds these buckets.</summary>
  /// <param name="opened">Buckets opened that were written.</param>
  /// <param name="closed">Buckets closed that were written.</param>
  internal void MarkLedgerTallied(long opened, long closed) {
    _ = Interlocked.Add(ref _talliedOpened, opened);
    _ = Interlocked.Add(ref _talliedClosed, closed);
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

  /// <summary>Buckets whose repair request is out and whose answer has not landed (#2029).</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  public long AnswerPendingBuckets { get; init; }

  /// <summary>Buckets a redelivery answered that wait for the next comparison (#2029).</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  public long AnsweredBuckets { get; init; }

  /// <summary>Integrity deliveries waiting for the maintainer's drain (#2035).</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  public long IntegrityDeliveriesPending { get; init; }

  /// <summary>Seconds the oldest waiting integrity delivery has waited (0 when none, #2035).</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  public double OldestIntegrityDeliveryAgeSeconds { get; init; }

  /// <summary>The deficit events, summed, of the deficit buckets repair can still act on: not unhealable and not
  /// exhausted (#2203). The data left to heal; 0 when none, and a bucket whose deficit is unknown adds nothing.</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerHealRemainingGaugesSqlTests.cs:Summary_SumsTheDeficitOfRepairableBuckets_AndCountsTheUnhealableOnesAsync</tests>
  public long UnhealedDeficitEvents { get; init; }

  /// <summary>Ledger rows marked unhealable, exhausted or not (#2203).</summary>
  /// <docs>resilience/stream-integrity#integrity-gauges</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerHealRemainingGaugesSqlTests.cs:Summary_SumsTheDeficitOfRepairableBuckets_AndCountsTheUnhealableOnesAsync</tests>
  public long UnhealableBuckets { get; init; }

  /// <summary>Unhealed buckets whose comparison found fewer events here than at the origin: the only kind a redelivery
  /// heals (#2260).</summary>
  /// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
  public long DeficitBuckets { get; init; }

  /// <summary>Unhealed buckets whose comparison found more events here than at the origin (#2260). Repair cannot remove
  /// them: asking the origin for more adds nothing, and local history is never deleted on a remote's say-so.</summary>
  /// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
  public long SurplusBuckets { get; init; }

  /// <summary>Unhealed buckets whose two sides hold the same number of events with different digests: the same events
  /// under different ids (#2260). A redelivery re-ships what is already here, so repair cannot heal them.</summary>
  /// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
  public long DigestMismatchBuckets { get; init; }

  /// <summary>Unhealed buckets whose kind no comparison has stated yet (#2260): rows written before kinds were recorded,
  /// or by the single-key report. The next comparison that sights the bucket states it.</summary>
  /// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
  public long UnknownKindBuckets { get; init; }

  /// <summary>Per-origin verified watermarks (one gauge series each). Empty until the collector
  /// reads the seals — a tiny table it visits in the same breath as the ledger summary.</summary>
  public IReadOnlyList<OriginSeal> Seals { get; init; } = [];
}

/// <summary>One origin's verified watermark, for the <c>sealed_through</c> gauge.</summary>
/// <param name="OriginServiceId">The audited origin.</param>
/// <param name="SealedThrough">Exclusive end of the highest window that audited clean and complete.</param>
/// <param name="LatestCommitSequence">The highest commit sequence the origin's checkpoints announced (inclusive), for
/// <c>verified_percent</c> (#2261); null when no checkpoint announced one.</param>
/// <docs>resilience/stream-integrity</docs>
public sealed record OriginSeal(Guid OriginServiceId, long SealedThrough, long? LatestCommitSequence = null);

/// <summary>An atomically swappable reading of the paced backfill, for its gauges (#2092).</summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
public sealed record BackfillGaugeSnapshot {
  /// <summary>Nothing running (also the reading before the first tick).</summary>
  public static BackfillGaugeSnapshot Empty { get; } = new();

  /// <summary>Windows requested and awaiting their answer.</summary>
  public int WindowsInFlight { get; init; }

  /// <summary>Each running backfill's estimated remaining events, by event type.</summary>
  public IReadOnlyList<BackfillRemaining> Remaining { get; init; } = [];
}

/// <summary>One running backfill's estimated remaining events, for the <c>backfill_remaining_events</c> gauge.</summary>
/// <param name="EventType">The event type.</param>
/// <param name="Events">Events estimated to remain.</param>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
public sealed record BackfillRemaining(string EventType, long Events);

/// <summary>
/// An atomically swappable reading of the open repair backlog (#2256), for the <c>ledger_open_buckets</c> and
/// <c>outbound_deliveries</c> gauges. Read only by the instance whose turn it is to do the maintainer's work.
/// </summary>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
public sealed record LedgerBacklogSnapshot {
  /// <summary>No reading: this instance does not report the backlog (another holds the duty, the read failed, or the
  /// engine keeps no ledger). The gauges report no series, never a zero.</summary>
  public static LedgerBacklogSnapshot NotReported { get; } = new();

  /// <summary>Whether this reading is reported at all.</summary>
  public bool Reported { get; init; }

  /// <summary>The open buckets, one entry per origin and state.</summary>
  public IReadOnlyList<LedgerBacklogCount> Buckets { get; init; } = [];

  /// <summary>Integrity deliveries waiting in the queue the maintainer drains.</summary>
  public long OutboundDeliveries { get; init; }
}

/// <summary>The open buckets of one origin in one state, for the <c>ledger_open_buckets</c> gauge (#2256).</summary>
/// <param name="Origin">The origin's service name, or its id when its name is not known.</param>
/// <param name="State">One of <see cref="LedgerBacklogStates"/>.</param>
/// <param name="Buckets">Buckets in that state.</param>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
public sealed record LedgerBacklogCount(string Origin, string State, long Buckets);

/// <summary>
/// The <c>state</c> tag values of the <c>ledger_open_buckets</c> gauge (#2256). Each bucket is in exactly one, decided in
/// this order: unhealable, exhausted, waiting, answered, open.
/// </summary>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
public static class LedgerBacklogStates {
  /// <summary>Marked as a bucket repair cannot change.</summary>
  public const string UNHEALABLE = "unhealable";

  /// <summary>Spent its repair budget; repair asks no more.</summary>
  public const string EXHAUSTED = "exhausted";

  /// <summary>A repair request is out and its answer has not landed.</summary>
  public const string WAITING = "waiting";

  /// <summary>Answered, waiting for the next comparison to decide healed or still divergent.</summary>
  public const string ANSWERED = "answered";

  /// <summary>Everything else: divergent and waiting its turn in the repair drain or the next comparison.</summary>
  public const string OPEN = "open";
}

/// <summary>
/// The <c>kind</c> tag values of the <c>unhealed_buckets_by_kind</c> gauge (#2260). Each unhealed bucket is exactly one;
/// the four add up to <c>unhealed_buckets</c>.
/// </summary>
/// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
public static class LedgerBucketKinds {
  /// <summary>The tag key.</summary>
  public const string TAG = "kind";

  /// <summary>Fewer events here than at the origin: data genuinely missing, and the only kind repair heals.</summary>
  public const string DEFICIT = "deficit";

  /// <summary>More events here than at the origin.</summary>
  public const string SURPLUS = "surplus";

  /// <summary>The same number of events on both sides with different digests: the same events under different ids.</summary>
  public const string DIGEST_MISMATCH = "digest_mismatch";

  /// <summary>No comparison has stated the bucket's kind yet.</summary>
  public const string UNKNOWN = "unknown";
}

/// <summary>
/// The <c>verified_percent</c> gauges' reading (#2261), computed by <see cref="IntegrityCompletion.Verified"/> from the
/// seals the ledger gauges read.
/// </summary>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public sealed record IntegrityVerifiedReading {
  /// <summary>No reading: the gauge is switched off, or nothing has been read. Both gauges report no series.</summary>
  public static IntegrityVerifiedReading NotReported { get; } = new();

  /// <summary>Whether this reading is reported at all.</summary>
  public bool Reported { get; init; }

  /// <summary>Each origin whose latest commit sequence is known, with its verified percentage.</summary>
  public IReadOnlyList<OriginVerifiedPercent> Origins { get; init; } = [];

  /// <summary>All origins, weighted by the commit sequences each announced; null when none is known.</summary>
  public double? AllOrigins { get; init; }
}

/// <summary>One origin's verified percentage, for the <c>verified_percent</c> gauge (#2261).</summary>
/// <param name="OriginServiceId">The origin.</param>
/// <param name="Percent">Its verified sequences as a percentage of its latest commit sequence, 0 to 100.</param>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public sealed record OriginVerifiedPercent(Guid OriginServiceId, double Percent);

/// <summary>
/// The <c>repair_progress_percent</c> and <c>backlog_eta_seconds</c> gauges' reading (#2261), computed over the rolling
/// window by the instance that reports the backlog.
/// </summary>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public sealed record RepairProgressReading {
  /// <summary>No reading: another instance reports it, it is switched off, or the tally could not be read.</summary>
  public static RepairProgressReading NotReported { get; } = new();

  /// <summary>Whether this reading is reported at all.</summary>
  public bool Reported { get; init; }

  /// <summary>Buckets closed as a percentage of buckets opened over the window; null when none opened, or switched
  /// off.</summary>
  public double? ProgressPercent { get; init; }

  /// <summary>Seconds until the repairable backlog empties at the window's net close rate; null while the backlog is
  /// flat or growing, when the backlog was not read, or switched off.</summary>
  public double? EtaSeconds { get; init; }
}
