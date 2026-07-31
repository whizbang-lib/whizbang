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
/// Stream-integrity tuning. Checkpoints are ON by default — integrity verification should not
/// require opting in; disable explicitly for hosts that genuinely do not want it.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public sealed class StreamIntegrityOptions {
  /// <summary>Publish periodic continuity checkpoints (default true).</summary>
  public bool CheckpointsEnabled { get; set; } = true;

  /// <summary>Checkpoint cadence in seconds (default 60).</summary>
  public int CheckpointIntervalSeconds { get; set; } = 60;
}
