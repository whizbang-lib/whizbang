using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity Phase B: the consumer-side gap state — per-origin PENDING deficits awaiting
/// two-cycle confirmation, and per-origin checkpoint liveness. In-memory by design: a restart
/// re-baselines (the deep-audit phases cover history), and a pending deficit that heals in flight
/// simply never confirms. Bounded: pending entries are capped; overflow drops the OLDEST pending
/// (it re-detects on a later cycle if still real).
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityGapTrackerTests.cs</tests>
public sealed class IntegrityGapTracker {
  /// <summary>One deficit awaiting confirmation on the next checkpoint.</summary>
  public sealed record PendingGap {
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
  }

  private const int MAX_PENDING = 1_000;
  private readonly Lock _lock = new();
  private readonly List<PendingGap> _pending = [];
  private readonly ConcurrentDictionary<Guid, (string Name, DateTimeOffset LastSeen)> _origins = new();

  /// <summary>Records a checkpoint arrival for liveness accounting.</summary>
  public void RecordCheckpoint(Guid originServiceId, string originServiceName, DateTimeOffset now) =>
    _origins[originServiceId] = (originServiceName, now);

  /// <summary>Adds a deficit awaiting confirmation on the origin's NEXT checkpoint.</summary>
  public void AddPending(PendingGap gap) {
    ArgumentNullException.ThrowIfNull(gap);
    lock (_lock) {
      if (_pending.Count >= MAX_PENDING) {
        _pending.RemoveAt(0);
      }
      _pending.Add(gap);
    }
  }

  /// <summary>Removes and returns every pending deficit against the given origin.</summary>
  public IReadOnlyList<PendingGap> TakePending(Guid originServiceId) {
    lock (_lock) {
      var taken = _pending.Where(p => p.OriginServiceId == originServiceId).ToList();
      _pending.RemoveAll(p => p.OriginServiceId == originServiceId);
      return taken;
    }
  }

  /// <summary>Every origin this consumer has seen a checkpoint from — the deep audit's origin set
  /// (an origin that never checkpoints is already a liveness alarm, not an audit target).</summary>
  public IReadOnlyList<(Guid OriginServiceId, string OriginServiceName)> GetOrigins() =>
    [.. _origins.Select(kv => (kv.Key, kv.Value.Name))];

  /// <summary>
  /// Origins whose last checkpoint is older than <paramref name="staleAfter"/> — the liveness
  /// alarm surface ("the origin stopped checkpointing", 3× interval by convention).
  /// </summary>
  public IReadOnlyList<(Guid OriginServiceId, string OriginServiceName, DateTimeOffset LastSeen)> GetStaleOrigins(
      TimeSpan staleAfter, DateTimeOffset now) =>
    [.. _origins
      .Where(kv => now - kv.Value.LastSeen > staleAfter)
      .Select(kv => (kv.Key, kv.Value.Name, kv.Value.LastSeen))];
}
