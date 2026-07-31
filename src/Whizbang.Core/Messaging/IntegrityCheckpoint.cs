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
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
[PinnedId("7d2a9c4e-5b83-4f1a-9e67-0c8d3b2a1f54")]
[Ephemeral]
public sealed record IntegrityCheckpoint : IEvent {
  /// <summary>The checkpoint stream — the origin's service id (one stream per origin).</summary>
  [StreamId]
  public required Guid CheckpointStreamId { get; init; }

  /// <summary>The origin service's stable id (matches every event's <c>SourceServiceId</c>).</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical service NAME — the directed-message Target a consumer uses to
  /// send a repair request back (<see cref="RequestRedeliveryCommand"/>).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Exclusive window floor (the previous checkpoint's watermark).</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive window watermark (the highest STAMPED commit sequence at publish time).</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>Per-(tenant, type) emission counts inside the window. Empty = quiet window.</summary>
  public List<CheckpointBucket> Buckets { get; init; } = [];
}

/// <summary>One (tenant, event type) emission count inside a checkpoint window.</summary>
/// <docs>proposals/stream-integrity</docs>
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
/// <docs>proposals/stream-integrity</docs>
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
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
[PinnedId("a1e6d8c2-3f7b-4a95-8d21-6e4c9b0f7a38")]
public sealed record IntegrityGapDetected : IEvent {
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
/// <docs>proposals/stream-integrity</docs>
public enum IntegrityRepairMode {
  /// <summary>Report confirmed gaps (default) — an operator decides what to repair.</summary>
  ReportOnly = 0,

  /// <summary>Report AND send a scoped re-delivery request per confirmed gap, capped per checkpoint.</summary>
  AutoRepairCapped = 1,
}

/// <summary>
/// Stream-integrity Phase A: one digest bucket — the order-independent identity hash of a
/// (tenant, type, stream)'s events. Two-lane 64-bit XOR of <c>hashtextextended(event_id, seed)</c>
/// with seeds 0/1: 128-bit-equivalent collision resistance, self-inverse (deletions need no
/// bookkeeping), arrival-order independent (origins fold in commit order, consumers in receive
/// order — same digest). Computed on demand at audit time; never maintained incrementally.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public sealed record StreamDigest {
  /// <summary>Tenant scope (<c>t</c> key), or null for unscoped events.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>The stream.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>XOR lane 0 (seed 0).</summary>
  public required long DigestLo { get; init; }

  /// <summary>XOR lane 1 (seed 1).</summary>
  public required long DigestHi { get; init; }

  /// <summary>Events folded into the digest.</summary>
  public required int EventCount { get; init; }
}

/// <summary>
/// Stream-integrity Phase S: one row of the consumed-type registry — when an event type joined
/// this service's consumed set, and where its backfill stands.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public sealed record ConsumedTypeRegistration {
  /// <summary>Stored event type name (catalog wire name).</summary>
  public required string EventType { get; init; }

  /// <summary>The expansion's backfill lifecycle position.</summary>
  public required ConsumedTypeBackfillStatus Status { get; init; }
}

/// <summary>
/// Stream-integrity Phase S: the backfill lifecycle of a consumed-type registration.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public enum ConsumedTypeBackfillStatus {
  /// <summary>Registered on FIRST boot — nothing existed to miss, no backfill.</summary>
  Baseline = 0,

  /// <summary>Expansion detected, backfill not yet requested — the audit surface when backfill is disabled.</summary>
  Pending = 1,

  /// <summary>The broadcast re-delivery request was sent (completion graduates via the audit phases).</summary>
  Requested = 2,
}

/// <summary>
/// Stream-integrity tuning. Checkpoints and gap detection are ON by default — integrity
/// verification should not require opting in; disable explicitly for hosts that genuinely do not
/// want it. Repair stays at <see cref="IntegrityRepairMode.ReportOnly"/> until an operator climbs
/// the ladder.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public sealed class StreamIntegrityOptions {
  /// <summary>Publish periodic continuity checkpoints (default true).</summary>
  public bool CheckpointsEnabled { get; set; } = true;

  /// <summary>Checkpoint cadence in seconds (default 60).</summary>
  public int CheckpointIntervalSeconds { get; set; } = 60;

  /// <summary>Verify received counts against other origins' checkpoints (default true).</summary>
  public bool GapDetectionEnabled { get; set; } = true;

  /// <summary>What to do with a CONFIRMED gap (default ReportOnly — the ladder's bottom rung).</summary>
  public IntegrityRepairMode RepairMode { get; set; } = IntegrityRepairMode.ReportOnly;

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

  /// <summary>Phase A: both sides fold only events older than this (minutes, default 60) — an
  /// in-flight delivery must never read as divergence.</summary>
  public int AuditSettleWindowMinutes { get; set; } = 60;

  /// <summary>Phase A: digest rows per manifest chunk (default 500 — bounded payloads).</summary>
  public int MaxDigestsPerManifest { get; set; } = 500;

  /// <summary>Phase A: storm cap on stream-scoped repair requests per received manifest chunk (default 25).</summary>
  public int MaxAutoRepairRequestsPerAudit { get; set; } = 25;

  /// <summary>Phase L: storm cap on local rebuilds dispatched per audit cycle (default 5).</summary>
  public int MaxAutoRebuildsPerAudit { get; set; } = 5;
}
