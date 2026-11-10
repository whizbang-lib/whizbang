// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
      description: "Divergent buckets currently unhealed (ledger rows). Falls as repair works — a heal deletes the row");
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

    // Issue #711: closed tag domains exist at zero from construction (see PassiveCounter).
    RepairsRequested.Touch("source", _repairSources);
    ManifestChunksSent.Touch("level", Enum.GetNames<Whizbang.Core.Messaging.ManifestLevel>());
    DigestDriftHealed.Touch("kind", _driftKinds);
    RepairTrafficDiscarded.Touch("role", _discardRoles);
    RepairsDeferred.Touch("reason", _deferralReasons);
    BackfillsEnded.Touch("outcome", _backfillOutcomes);
    RedeliveryAnswersCapped.Touch("kind", _cappedKinds);
  }

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

  /// <summary>Per-origin verified watermarks (one gauge series each). Empty until the collector
  /// reads the seals — a tiny table it visits in the same breath as the ledger summary.</summary>
  public IReadOnlyList<OriginSeal> Seals { get; init; } = [];
}

/// <summary>One origin's verified watermark, for the <c>sealed_through</c> gauge.</summary>
/// <param name="OriginServiceId">The audited origin.</param>
/// <param name="SealedThrough">Exclusive end of the highest window that audited clean and complete.</param>
/// <docs>resilience/stream-integrity</docs>
public sealed record OriginSeal(Guid OriginServiceId, long SealedThrough);

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
