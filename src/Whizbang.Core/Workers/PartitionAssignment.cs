// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// The partition assignment the elected assigner publishes: which instance each partition belongs to, under which
/// tenure of the assigner role, and until when it holds.
/// </summary>
/// <remarks>
/// <para>
/// Partition <c>p</c> belongs to <c>Members[p % Members.Count]</c>: an instance's rank is its position in
/// <see cref="Members"/> and the member count is the instance count, which is the arithmetic every acquisition already
/// applies. The assigner decides liveness once, for everyone, so claimers stop ranking the peers they believe are alive
/// and stop overlapping when that belief is wrong.
/// </para>
/// <para>
/// <see cref="Epoch"/> is the role election's epoch for the assigner's tenure; <see cref="Revision"/> counts publishes
/// within it. The pair is the assignment's version: the store refuses a publish from a deposed assigner, and the
/// claim accepts a cached copy only while its version is still the published one and its lease has not run out.
/// </para>
/// </remarks>
/// <param name="Epoch">The role epoch of the tenure that published this assignment.</param>
/// <param name="Revision">The publish count within <paramref name="Epoch"/>, from 1.</param>
/// <param name="AssignerInstanceId">The instance that published it.</param>
/// <param name="Members">The live instances, in rank order.</param>
/// <param name="PublishedAt">When it was published, by the database clock.</param>
/// <param name="LeaseExpiresAt">When it stops being valid unless the assigner renews it, by the database clock.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignmentTests.cs:RankOf_AMember_IsItsPositionAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignmentTests.cs:OwnerOf_APartition_IsTheMemberAtItsResidueAsync</tests>
public sealed record PartitionAssignment(
  long Epoch,
  long Revision,
  Guid AssignerInstanceId,
  IReadOnlyList<Guid> Members,
  DateTimeOffset PublishedAt,
  DateTimeOffset LeaseExpiresAt) {

  /// <summary>Two assignments are equal when every field is, the members compared in order.</summary>
  /// <param name="other">The other assignment.</param>
  /// <returns>True when equal.</returns>
  public bool Equals(PartitionAssignment? other) =>
    other is not null
    && Epoch == other.Epoch
    && Revision == other.Revision
    && AssignerInstanceId == other.AssignerInstanceId
    && PublishedAt == other.PublishedAt
    && LeaseExpiresAt == other.LeaseExpiresAt
    && Members.SequenceEqual(other.Members);

  /// <inheritdoc />
  public override int GetHashCode() => HashCode.Combine(Epoch, Revision, AssignerInstanceId);

  /// <summary>The version a claim presents: the epoch and revision together.</summary>
  public PartitionAssignmentVersion Version => new(Epoch, Revision);

  /// <summary>The rank of <paramref name="instanceId"/>, or <see langword="null"/> when it is not a member.</summary>
  /// <param name="instanceId">The instance.</param>
  /// <returns>Its zero-based position in <see cref="Members"/>, or null.</returns>
  public int? RankOf(Guid instanceId) {
    for (var i = 0; i < Members.Count; i++) {
      if (Members[i] == instanceId) {
        return i;
      }
    }
    return null;
  }

  /// <summary>The instance a partition belongs to.</summary>
  /// <param name="partitionNumber">A partition number, zero or more.</param>
  /// <returns>The member at the partition's residue.</returns>
  /// <exception cref="InvalidOperationException">The assignment has no members.</exception>
  public Guid OwnerOf(int partitionNumber) {
    ArgumentOutOfRangeException.ThrowIfNegative(partitionNumber);
    if (Members.Count == 0) {
      throw new InvalidOperationException("The assignment has no members.");
    }
    return Members[partitionNumber % Members.Count];
  }
}

/// <summary>An assignment's version: the role epoch of its tenure and its revision within that tenure.</summary>
/// <param name="Epoch">The role epoch.</param>
/// <param name="Revision">The revision within the epoch.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public readonly record struct PartitionAssignmentVersion(long Epoch, long Revision);

/// <summary>How an instance reaches the database, recorded at registration so the assigner judges it by the right signal.</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public enum InstanceConnectionMode {
  /// <summary>Pooled connections only: no session can be seen, so the instance is judged by its heartbeat.</summary>
  Pooled = 0,

  /// <summary>
  /// A dedicated direct connection holding the instance's session alive-lock, which is the authoritative liveness
  /// signal; the heartbeat is the fallback while the lock is not held.
  /// </summary>
  Direct = 1,
}

/// <summary>The database spelling of <see cref="InstanceConnectionMode"/>.</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public static class InstanceConnectionModes {
  /// <summary>The stored value of <see cref="InstanceConnectionMode.Pooled"/>.</summary>
  public const string POOLED = "pooled";

  /// <summary>The stored value of <see cref="InstanceConnectionMode.Direct"/>.</summary>
  public const string DIRECT = "direct";

  /// <summary>The stored value of a mode.</summary>
  /// <param name="mode">The mode.</param>
  /// <returns><see cref="DIRECT"/> or <see cref="POOLED"/>.</returns>
  public static string ToDatabaseValue(InstanceConnectionMode mode) =>
    mode == InstanceConnectionMode.Direct ? DIRECT : POOLED;

  /// <summary>Reads a stored value. Anything but <see cref="DIRECT"/>, including null, is pooled.</summary>
  /// <param name="value">The stored value.</param>
  /// <returns>The mode.</returns>
  public static InstanceConnectionMode Parse(string? value) =>
    string.Equals(value, DIRECT, StringComparison.Ordinal) ? InstanceConnectionMode.Direct : InstanceConnectionMode.Pooled;
}

/// <summary>What the assigner judges one registered instance by.</summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="Mode">How it reaches the database, as it registered.</param>
/// <param name="AliveLockHeld">Whether its alive-lock is held; only ever true in <see cref="InstanceConnectionMode.Direct"/>.</param>
/// <param name="HeartbeatAge">How long ago it last beat, by the database clock.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public sealed record PartitionAssignmentCandidate(
  Guid InstanceId,
  InstanceConnectionMode Mode,
  bool AliveLockHeld,
  TimeSpan HeartbeatAge);

/// <summary>A read of the published assignment, with the lease left on it by the database clock.</summary>
/// <param name="Assignment">The assignment.</param>
/// <param name="LeaseRemaining">How long until its lease runs out, by the database clock; negative once it has.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public sealed record PartitionAssignmentRead(PartitionAssignment Assignment, TimeSpan LeaseRemaining);

/// <summary>
/// The store behind the partition assigner: the published assignment, the candidates' liveness signals, and the
/// fenced writes.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public interface IPartitionAssignmentStore {
  /// <summary>Reads the published assignment, one keyed read. Null when none has been published.</summary>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The assignment and its remaining lease, or null.</returns>
  Task<PartitionAssignmentRead?> ReadAsync(CancellationToken cancellationToken);

  /// <summary>Every registered, not evicted instance with the signals its connection mode is judged by.</summary>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The candidates, ordered by instance id.</returns>
  Task<IReadOnlyList<PartitionAssignmentCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Publishes an assignment, fenced by the role epoch: only the current holder of <paramref name="role"/> at
  /// <paramref name="epoch"/> can. Returns the published assignment, or null when the fence refused it.
  /// </summary>
  /// <param name="role">The assigner role.</param>
  /// <param name="instanceId">The publishing instance.</param>
  /// <param name="epoch">Its role epoch.</param>
  /// <param name="members">The live instances, in rank order.</param>
  /// <param name="lease">How long the assignment holds without renewal.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The assignment as published, or null when the caller no longer holds the role at that epoch.</returns>
  Task<PartitionAssignment?> PublishAsync(
    string role, Guid instanceId, long epoch, IReadOnlyList<Guid> members, TimeSpan lease, CancellationToken cancellationToken);

  /// <summary>Extends the lease of the assignment the caller published at <paramref name="epoch"/>, while it holds the role.</summary>
  /// <param name="instanceId">The assigner.</param>
  /// <param name="epoch">Its role epoch.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>True when the lease was extended.</returns>
  Task<bool> RenewAsync(Guid instanceId, long epoch, CancellationToken cancellationToken);
}

/// <summary>
/// A claimer's view of the published partition assignment: cached in memory, refreshed when the assigner publishes
/// (or when a claim reports its copy stale), and used until it expires.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public interface IPartitionAssignmentSource {
  /// <summary>The cached assignment, or null when none has been read.</summary>
  PartitionAssignment? Current { get; }

  /// <summary>Raised when the cached assignment changes to a new version (or to none). Must not block.</summary>
  event Action<PartitionAssignment?>? OnAssignmentChanged;

  /// <summary>Marks the cached copy stale; the next <see cref="ForClaimAsync"/> refreshes it with one keyed read.</summary>
  void MarkStale();

  /// <summary>
  /// The version a claim by <paramref name="instanceId"/> should present, refreshing the cache first when it is stale.
  /// Null when there is no assignment, it has expired, or the instance is not a member: the claim then ranks itself.
  /// </summary>
  /// <param name="instanceId">The claiming instance.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The version to present, or null.</returns>
  ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken);
}
