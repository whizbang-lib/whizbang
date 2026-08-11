using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity Phase B: the origin's periodic continuity checkpoint. "Between commit-sequence
/// watermark <see cref="FromCommitSequence"/> (exclusive) and <see cref="ToCommitSequence"/>
/// (inclusive), I emitted these per-(tenant, type) counts." Consumers count the events they have
/// persisted from this origin inside the same window — keyed by the per-event
/// <c>SourceCommitSequence</c> every live delivery already carries — and compare, for the types
/// they subscribe to. A deficit persisting past the NEXT checkpoint is a confirmed gap; a missing
/// checkpoint (3× interval) is a liveness alarm.
/// </summary>
/// <remarks>
/// <c>[Ephemeral]</c>: a checkpoint is a transient control signal — once every consumer has
/// consumed it, its body self-destructs on the standard consumption-gated path. The checkpoint
/// stream is the ORIGIN's service id (one homogeneous ephemeral stream per origin). Checkpoints
/// publish even when the window is empty — absence is the alarm, so silence must be abnormal.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
[PinnedId("7d2a9c4e-5b83-4f1a-9e67-0c8d3b2a1f54")]
[Ephemeral]
public sealed record IntegrityCheckpoint : IEvent, IControlPlaneMessage {
  /// <summary>The checkpoint stream — the origin's service id (one stream per origin).</summary>
  [StreamId]
  public required Guid CheckpointStreamId { get; init; }

  /// <summary>The origin service's stable id (matches every event's <c>SourceServiceId</c>).</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical service NAME — the directed-message Target a consumer uses to
  /// send a repair request back (<see cref="RequestRedeliveryCommand"/>).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>
  /// A topic THIS ORIGIN consumes — the address a consumer publishes directed integrity
  /// requests (manifest / redelivery / drill-down) to. Carried on the checkpoint because the
  /// requester cannot guess an origin-reachable topic in a domain-scoped topology; the origin
  /// is the only party that knows where it listens. Null from older origins — requesters fall
  /// back to their legacy behavior.
  /// </summary>
  public string? RequestTopic { get; init; }

  /// <summary>Exclusive window floor (the previous checkpoint's watermark).</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive window watermark (the highest STAMPED commit sequence at publish time).</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>Per-(tenant, type) emission counts inside the window. Empty = quiet window.</summary>
  public List<CheckpointBucket> Buckets { get; init; } = [];
}

/// <summary>One (tenant, event type) emission count inside a checkpoint window.</summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record CheckpointBucket {
  /// <summary>Tenant scope (<c>t</c> key), or null for unscoped events.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>Events of this (tenant, type) emitted inside the window.</summary>
  public required int Count { get; init; }
}

/// <summary>
/// One advanced checkpoint window, as returned by
/// <see cref="IWorkCoordinator.AdvanceIntegrityCheckpointAsync"/>: the origin's previous watermark
/// (exclusive), the new watermark (inclusive), and the per-(tenant, type) counts between them.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record IntegrityCheckpointWindow {
  /// <summary>Exclusive window floor.</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive watermark.</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>Per-(tenant, type) counts. Empty for a quiet or baseline window.</summary>
  public IReadOnlyList<CheckpointBucket> Buckets { get; init; } = [];
}

/// <summary>
/// Stream-integrity Phase B: a CONFIRMED continuity gap — a per-(tenant, type) receipt deficit
/// against an origin's checkpoint window that persisted past the NEXT checkpoint (two-cycle
/// confirmation absorbs in-flight stragglers). This is the ops report; when the repair ladder is
/// at <see cref="IntegrityRepairMode.AutoRepairCapped"/> the consumer also sends a scoped
/// <see cref="RequestRedeliveryCommand"/> for exactly this window.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
[PinnedId("a1e6d8c2-3f7b-4a95-8d21-6e4c9b0f7a38")]
public sealed record IntegrityGapDetected : IEvent, IControlPlaneMessage {
  /// <summary>Report stream (one stream per report — reports are standalone ops facts).</summary>
  [StreamId]
  public required Guid ReportStreamId { get; init; }

  /// <summary>The origin the deficit is against.</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical name (the repair Target).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Tenant scope of the deficit bucket (null = unscoped).</summary>
  public string? TenantScope { get; init; }

  /// <summary>Event type of the deficit bucket.</summary>
  public required string EventType { get; init; }

  /// <summary>Exclusive window floor.</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive window watermark.</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>The origin's emission count for the bucket.</summary>
  public required int ExpectedCount { get; init; }

  /// <summary>This consumer's persisted receipt count for the bucket.</summary>
  public required int ActualCount { get; init; }

  /// <summary>True when a scoped repair request was sent (ladder at AutoRepairCapped).</summary>
  public bool AutoRepairRequested { get; init; }
}

/// <summary>
/// The repair ladder position for confirmed gaps (stream-integrity Phase B).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public enum IntegrityRepairMode {
  /// <summary>Report confirmed gaps only — the explicit opt-DOWN for operators who want
  /// report-and-decide (every report still states exactly what auto-repair would have done).</summary>
  ReportOnly = 0,

  /// <summary>Report AND repair (default): a scoped re-delivery request / local rebuild per
  /// confirmed gap, hard-capped at every rung so a mass divergence can never storm.</summary>
  AutoRepairCapped = 1,
}

/// <summary>
/// Stream-integrity Phase A: one digest bucket — the order-independent identity hash of a
/// (tenant, type, stream)'s events. Two-lane 64-bit XOR of <c>hashtextextended(event_id, seed)</c>
/// with seeds 0/1: 128-bit-equivalent collision resistance, self-inverse (deletions need no
/// bookkeeping), arrival-order independent (origins fold in commit order, consumers in receive
/// order — same digest). A1c: maintained INCREMENTALLY in <c>wh_stream_digests</c> by the write
/// paths (emit-chain folds, close/reclassify subtraction) and read via
/// <see cref="IWorkCoordinator.GetStreamDigestsAsync"/>; the on-demand recompute
/// (<see cref="IWorkCoordinator.ComputeStreamDigestsAsync"/>) remains the trust-but-verify sweep.
/// At <see cref="ManifestLevel.Types"/> the same record carries a per-(tenant, type) roll-up with
/// <see cref="StreamId"/> = <see cref="Guid.Empty"/> — valid because stream buckets partition the
/// type's events, so XOR-ing them equals folding every event of the type.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record StreamDigest {
  /// <summary>Tenant scope (<c>t</c> key), or null for unscoped events.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>The stream, or <see cref="Guid.Empty"/> for a type-level roll-up.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>XOR lane 0 (seed 0).</summary>
  public required long DigestLo { get; init; }

  /// <summary>XOR lane 1 (seed 1).</summary>
  public required long DigestHi { get; init; }

  /// <summary>Events folded into the digest.</summary>
  public required int EventCount { get; init; }

  /// <summary>When the bucket last changed (table reads only; null on recomputed rows). Both
  /// audit sides skip comparing buckets updated inside the settle window — the incremental
  /// equivalent of the recompute's created-at settle filter.</summary>
  public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Stream-integrity A1c: the outcome of one <see cref="IWorkCoordinator.VerifyDigestTableAsync"/>
/// pass — the trust-but-verify reconcile of the incrementally-maintained digest table against a
/// full recompute. Any non-zero drift means an unaccounted write path touched audited rows; the
/// pass HEALS the table (update/remove/add to match the recompute) and the caller alarms.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record DigestVerificationResult {
  /// <summary>Settled buckets the pass checked.</summary>
  public required int BucketsChecked { get; init; }

  /// <summary>Buckets whose digest/count disagreed with the recompute (healed in place).</summary>
  public required int DriftUpdated { get; init; }

  /// <summary>Phantom buckets with no backing events (removed).</summary>
  public required int DriftRemoved { get; init; }

  /// <summary>Buckets the table was missing entirely (added).</summary>
  public required int DriftAdded { get; init; }

  /// <summary>Total drifted buckets — zero means the incremental maintenance is provably clean.</summary>
  public int TotalDrift => DriftUpdated + DriftRemoved + DriftAdded;
}

/// <summary>
/// #80-D: result of the sweep's SEAL backstop — each closed digest epoch recomputed from the
/// store, compared bucket-for-bucket, and refolded on drift. Manifest answers trust seals
/// without re-verifying, so this pass is the one place a bad seal gets caught; non-zero
/// <paramref name="EpochsDrifted"/> means an unaccounted write path touched sealed history.
/// </summary>
/// <param name="EpochsChecked">Closed epochs the pass compared (unsettled-arrival epochs skip).</param>
/// <param name="EpochsDrifted">Epochs whose stored folds disagreed with the recompute (refolded).</param>
/// <docs>resilience/stream-integrity</docs>
public sealed record EpochVerificationResult(int EpochsChecked, int EpochsDrifted);

/// <summary>
/// Stream-integrity Phase S: one row of the consumed-type registry — when an event type joined
/// this service's consumed set, and where its backfill stands.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record ConsumedTypeRegistration {
  /// <summary>Stored event type name (catalog wire name).</summary>
  public required string EventType { get; init; }

  /// <summary>The expansion's backfill lifecycle position.</summary>
  public required ConsumedTypeBackfillStatus Status { get; init; }
}

/// <summary>
/// Stream-integrity Phase S: the backfill lifecycle of a consumed-type registration.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public enum ConsumedTypeBackfillStatus {
  /// <summary>Registered on FIRST boot — nothing existed to miss, no backfill.</summary>
  Baseline = 0,

  /// <summary>Expansion detected, backfill not yet requested — the audit surface when backfill is disabled.</summary>
  Pending = 1,

  /// <summary>The broadcast re-delivery request was sent (completion graduates via the audit phases).</summary>
  Requested = 2,
}

/// <summary>
/// Stream-integrity tuning. The out-of-the-box posture is SELF-HEALING: checkpoints, gap
/// detection, backfill, and the deep audit are ON, and repair runs at
/// <see cref="IntegrityRepairMode.AutoRepairCapped"/> — every rung hard-capped so a mass
/// divergence reports loudly instead of storming. Operators who want report-and-decide opt DOWN
/// to <see cref="IntegrityRepairMode.ReportOnly"/> (reports still state exactly what auto-repair
/// would have done).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed class StreamIntegrityOptions {
  /// <summary>Publish periodic continuity checkpoints (default true).</summary>
  public bool CheckpointsEnabled { get; set; } = true;

  /// <summary>Checkpoint cadence in seconds (default 60).</summary>
  public int CheckpointIntervalSeconds { get; set; } = 60;

  /// <summary>Verify received counts against other origins' checkpoints (default true).</summary>
  public bool GapDetectionEnabled { get; set; } = true;

  /// <summary>What to do with a CONFIRMED gap (default <see cref="IntegrityRepairMode.AutoRepairCapped"/>
  /// — self-healing out of the box; <see cref="IntegrityRepairMode.ReportOnly"/> is the opt-down).</summary>
  public IntegrityRepairMode RepairMode { get; set; } = IntegrityRepairMode.AutoRepairCapped;

  /// <summary>Storm cap: at most this many auto-repair requests per received checkpoint (default 10).</summary>
  public int MaxAutoRepairRequestsPerCheckpoint { get; set; } = 10;

  /// <summary>Wire topic repair requests publish to AND bundles return on. Null (default) = the
  /// consumer's first subscribed destination.</summary>
  public string? RepairTopic { get; set; }

  /// <summary>
  /// Phase S: when the consumed-type set GROWS (a later boot adds event types), broadcast a
  /// state-only re-delivery request for the new types' history (default true). Disabling still
  /// RECORDS the expansion as Pending — the audit reports "pending backfill", not divergence.
  /// </summary>
  public bool BackfillOnSubscriptionGrowth { get; set; } = true;

  /// <summary>Phase A/L: run the scheduled deep audit (default true).</summary>
  public bool AuditEnabled { get; set; } = true;

  /// <summary>Phase A/L: audit cadence in minutes (default 1440 — daily).</summary>
  public int AuditIntervalMinutes { get; set; } = 1440;

  /// <summary>
  /// Run the FIRST deep audit shortly after startup (default true) instead of waiting a full
  /// interval — historical divergence (a consumer that drifted before this boot) heals minutes
  /// after a deploy, not a day later. The startup audit fires after a 30-second floor plus a
  /// random splay of up to <see cref="StartupAuditMaxJitterSeconds"/>, so a fleet deploy's
  /// audits de-synchronize instead of storming; A1c's type-level exchange keeps each one at
  /// O(types) wire cost. False restores interval-first scheduling.
  /// </summary>
  public bool AuditOnStartup { get; set; } = true;

  /// <summary>Max random splay (seconds, default 300) added to the startup audit's 30-second
  /// floor — the deploy de-synchronizer.</summary>
  public int StartupAuditMaxJitterSeconds { get; set; } = 300;

  /// <summary>Phase A: both sides fold only events older than this (minutes, default 60) — an
  /// in-flight delivery must never read as divergence.</summary>
  public int AuditSettleWindowMinutes { get; set; } = 60;

  /// <summary>Phase A: digest rows per manifest chunk (default 500 — bounded payloads).</summary>
  public int MaxDigestsPerManifest { get; set; } = 500;

  /// <summary>Phase A: storm cap on stream-scoped repair requests per received manifest chunk (default 25).</summary>
  public int MaxAutoRepairRequestsPerAudit { get; set; } = 25;

  /// <summary>Phase L: storm cap on local rebuilds dispatched per audit cycle (default 5).</summary>
  public int MaxAutoRebuildsPerAudit { get; set; } = 5;

  /// <summary>
  /// Phase L: hard cap on coverage-gap REPORTS per audit cycle (default 100) — and the bound on
  /// the gap query itself. A systematically-uncovered perspective can surface thousands of gaps
  /// in one cycle; an unbounded report loop flooded a live consumer's dispatcher at startup and
  /// crashlooped the pod. The remainder re-audits next cycle as repairs shrink it.
  /// </summary>
  public int MaxCoverageGapReportsPerAudit { get; set; } = 100;

  /// <summary>
  /// Hard cap on divergence REPORTS published per manifest comparison (default 100). The sibling
  /// of <see cref="MaxCoverageGapReportsPerAudit"/>, and it exists for the identical reason: each
  /// report is a durable outbox write, so an unbounded per-stream loop is thousands of sequential
  /// database round-trips inside one message handler. That starved the host's HTTP pipeline until
  /// the always-healthy liveness endpoint stopped answering, and the fleet entered a restart loop
  /// — audit, starve, get killed, restart, audit again.
  /// <para>
  /// A manifest carries up to <see cref="MaxDigestsPerManifest"/> buckets, so the cap must sit
  /// below it to bound the fan-out. Suppressed divergences are not lost: the ledger keeps them
  /// unhealed, and the next comparison re-offers whatever is still divergent, so a persistent
  /// problem still converges — it just stops trying to name every stream in one breath.
  /// </para>
  /// </summary>
  public int MaxDivergenceReportsPerManifest { get; set; } = 100;


  /// <summary>
  /// Hard cap on confirmed-gap REPORTS published per received checkpoint (default 100). The third
  /// member of the same family as <see cref="MaxCoverageGapReportsPerAudit"/> and
  /// <see cref="MaxDivergenceReportsPerManifest"/>: <see cref="MaxAutoRepairRequestsPerCheckpoint"/>
  /// already bounded the repairs in that loop, but the report published alongside them ran free.
  /// Pending gaps are keyed by (tenant, event type), so their number grows with the deployment, not
  /// with any batch size — and each report is a durable outbox write on the same thread that owes
  /// the liveness probe an answer.
  /// <para>
  /// Suppressed gaps are not lost: an unhealed deficit is re-detected on the next checkpoint, so
  /// the condition keeps surfacing until it is actually repaired.
  /// </para>
  /// </summary>
  public int MaxGapReportsPerCheckpoint { get; set; } = 100;

  /// <summary>
  /// A1c: every Nth audit cycle is a FULL SWEEP (default 7 — weekly at the daily default): the
  /// worker verifies + heals its own digest table against a full recompute
  /// (<see cref="IWorkCoordinator.VerifyDigestTableAsync"/>) and the manifest exchange runs on
  /// recomputed digests end to end — covering buckets whose steady traffic settle-skips them on
  /// table-driven cycles. 0 or negative disables sweeps (table-driven cycles only).
  /// #80-D: this counter is the FALLBACK cadence — once <see cref="FullSweepCron"/> is registered
  /// on the temporal engine, the counter stands down and the cron owns the sweep.
  /// </summary>
  public int FullSweepEveryNthAudit { get; set; } = 7;

  /// <summary>
  /// #80-D: cron for the full sweep on the temporal engine (default <c>"0 3 * * *"</c> — daily at
  /// 03:00 UTC), so the heaviest verification runs at a configured IDLE hour instead of wherever
  /// the every-Nth-cycle counter happens to land it. When the cron's minute field is <c>0</c>
  /// (the default), each service replaces it with a stable per-service splay minute so a fleet
  /// sharing one database server does not sweep in unison; an explicit non-zero minute is honored
  /// verbatim. Null or empty disables cron scheduling —
  /// <see cref="FullSweepEveryNthAudit"/> then remains the cadence, as it does on hosts without
  /// the temporal engine.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public string? FullSweepCron { get; set; } = "0 3 * * *";

  /// <summary>
  /// #80-D: cap on closed epochs the sweep's seal backstop recomputes per sweep (default 10000).
  /// Manifest answers trust sealed epochs without re-verifying, so the sweep is the one place a
  /// bad seal gets caught — but the recompute is O(events-in-epoch) each, and a very large store
  /// finishes verification across several nightly sweeps rather than stalling one.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public int MaxEpochVerificationsPerSweep { get; set; } = 10_000;

  /// <summary>
  /// A1c: storm cap on the DRILL-DOWN — how many mismatched types one type-level manifest may
  /// escalate to stream-level manifest requests (default 10). Remaining mismatches re-audit next
  /// cycle; a healthy system drills down for zero.
  /// </summary>
  public int MaxDrillDownTypesPerAudit { get; set; } = 10;

  /// <summary>
  /// Minutes an UNCHANGED divergence stays silent after being reported (default 60). The same
  /// unhealed bucket re-detected on every audit cycle is cadence, not news — without this
  /// cooldown a persistent divergence floods the outbox with an
  /// <see cref="IntegrityDivergenceDetected"/> per bucket per cycle (observed live: tens of
  /// thousands of rows in hours, saturating a shared database server). A changed signature
  /// (either side's digest moved) always reports immediately.
  /// </summary>
  public int DivergenceReportCooldownMinutes { get; set; } = 60;

  /// <summary>
  /// Base seconds between repair requests for one divergent bucket (default 300); each further
  /// attempt doubles the wait. An origin that stays silent — down, or unable to help — is asked
  /// less and less often instead of hammered on every audit cycle.
  /// </summary>
  public int RepairRequestBackoffSeconds { get; set; } = 300;

  /// <summary>
  /// Repair attempts per divergent bucket before the requester stops asking (default 8). The
  /// divergence still re-reports at the cooldown cadence — past the cap it needs operator eyes,
  /// not an infinite request loop. A changed signature (progress or fresh damage) resets the
  /// budget.
  /// </summary>
  public int MaxRepairAttemptsPerBucket { get; set; } = 8;

  /// <summary>
  /// Advance the digest-epoch closure frontier on the maintenance cadence (default true).
  /// Epochs (migration 092) are what let manifest answers read immutable folds instead of
  /// re-aggregating live history; without closure the frontier never moves and every audit
  /// keeps paying the full-scan cost the epochs exist to end. Disable only on engines that
  /// serve no integrity manifests.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public bool EpochClosureEnabled { get; set; } = true;

  /// <summary>
  /// Max epochs closed per maintenance cycle across all lanes (default 64). Each closure is an
  /// O(events-in-epoch) recompute, so this bounds a cycle's closure work by the operator's cap
  /// rather than by backlog size — a long-unclosed store catches up over several cycles instead
  /// of stalling one. The settle window closure uses is <see cref="AuditSettleWindowMinutes"/>:
  /// closure and the audit MUST agree on what "settled" means, or a seal could disagree with a
  /// manifest folded over the same range.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public int MaxEpochClosuresPerMaintenanceCycle { get; set; } = 64;

  /// <summary>
  /// Publish <see cref="IntegrityDivergenceDetected"/> / <see cref="IntegrityGapDetected"/> as
  /// durable events (default <c>false</c>).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Stream integrity has two halves. REPAIR is a closed loop — a request goes to the origin, the
  /// origin answers, the divergence heals. REPORTING was an open loop: nothing in the framework
  /// consumes either report type, so every one published was a durable write that no code path
  /// ever read.
  /// </para>
  /// <para>
  /// The cost was not proportional to "some unread messages". Each report carries its own
  /// <c>ReportStreamId</c>, so it mints a NEW event stream — a stream row, an outbox row, an
  /// event-store pointer and body, and perspective work items, per sighting. With no consumer no
  /// cursor ever advances past them, so the consumption-gated reaper can never collect them
  /// either: not a backlog that drains, but unbounded permanent growth in the tables the work
  /// pump scans every poll.
  /// </para>
  /// <para>
  /// The state those reports described is what the durable ledger already holds — one row per
  /// divergent bucket, deduplicated, surviving restarts — and it is surfaced as gauges
  /// (<c>whizbang.integrity.unhealed_buckets</c> and friends) that go DOWN when things heal.
  /// A count of currently-broken things is what an operator can act on; a stream of past-tense
  /// notifications is not. Enable this only if you have a consumer that genuinely reacts.
  /// </para>
  /// </remarks>
  public bool PublishReportEvents { get; set; }

}
