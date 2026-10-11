// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>Options for the elected partition assigner (#1254).</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:Validate_ALeaseNoLongerThanTheSlowHeartbeat_IsRefusedAsync</tests>
public sealed class PartitionAssignerOptions {
  /// <summary>The role the assigner is elected to through the role election.</summary>
  public const string ROLE = "partition-assigner";

  /// <summary>
  /// Whether an assigner is elected and claimers use its assignment. Default true; it takes effect only where role
  /// assignment is enabled. Off, every claimer ranks its peers itself, as before.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// How long a pooled instance's heartbeat counts it as live. Default 90 seconds: three of the heartbeat's regular
  /// beats.
  /// </summary>
  public TimeSpan PooledHeartbeatWindow { get; set; } = TimeSpan.FromSeconds(90);

  /// <summary>
  /// How long a direct instance's heartbeat counts it as live while its alive-lock is not held (the fallback). Default
  /// 180 seconds: three of the slow beats a direct instance makes while it holds its lock, so an instance whose lock
  /// was just lost is not judged by a beat that was never due.
  /// </summary>
  public TimeSpan DirectHeartbeatWindow { get; set; } = TimeSpan.FromSeconds(180);

  /// <summary>
  /// How long a published assignment holds without renewal. The assigner renews it as part of its own liveness (its
  /// alive-lock tick, and its heartbeat), so this is also how long the assigner may go unseen before claimers stop
  /// using its assignment. Default 90 seconds; it must be longer than the slow heartbeat cadence.
  /// </summary>
  public TimeSpan AssignmentLease { get; set; } = TimeSpan.FromSeconds(90);

  /// <summary>
  /// The backstop: an assignment is republished at least this often even when nothing changed. Default 5 minutes.
  /// </summary>
  public TimeSpan RepublishInterval { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>Throws when the options cannot work together.</summary>
  /// <exception cref="InvalidOperationException">A window or lease is not positive, or the lease is too short.</exception>
  public void Validate() {
    if (PooledHeartbeatWindow <= TimeSpan.Zero || DirectHeartbeatWindow <= TimeSpan.Zero) {
      throw new InvalidOperationException("PartitionAssigner heartbeat windows must be positive.");
    }
    if (RepublishInterval <= TimeSpan.Zero) {
      throw new InvalidOperationException("PartitionAssigner.RepublishInterval must be positive.");
    }
    if (AssignmentLease <= TimeSpan.FromSeconds(60)) {
      throw new InvalidOperationException(
        "PartitionAssigner.AssignmentLease must be longer than the slow heartbeat cadence (60 seconds), or an assigner "
        + "holding its alive-lock would let its own assignment lapse between beats.");
    }
  }
}

/// <summary>Why the assigner published, or that it did not.</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public enum PartitionAssignerReason {
  /// <summary>Nothing changed and the backstop is not due.</summary>
  Unchanged = 0,

  /// <summary>No assignment had been published, or it is another tenure's.</summary>
  NewTenure = 1,

  /// <summary>An instance became live or stopped being live: an alive-lock gained or lost, a heartbeat fresh or stale.</summary>
  MembershipChanged = 2,

  /// <summary>Nothing changed, and the backstop cadence came due.</summary>
  Backstop = 3,
}

/// <summary>One evaluation by the assigner: the live members it found and whether that calls for a publish.</summary>
/// <param name="Members">The live instances, in rank order.</param>
/// <param name="Reason">Why it publishes, or <see cref="PartitionAssignerReason.Unchanged"/>.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public sealed record PartitionAssignerEvaluation(IReadOnlyList<Guid> Members, PartitionAssignerReason Reason) {
  /// <summary>Whether this evaluation publishes.</summary>
  public bool Publishes => Reason != PartitionAssignerReason.Unchanged;
}

/// <summary>
/// The assigner's decisions, with no I/O and no clock of their own: who is live, by the strategy for each connection
/// mode, and whether that calls for a publish.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:IsLive_ADirectInstanceHoldingItsLock_IsLiveWhateverItsHeartbeatAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:Evaluate_AMemberGoingStale_PublishesAsync</tests>
public static class PartitionAssigner {
  /// <summary>
  /// Whether a candidate is live, by the strategy for its connection mode. A direct instance's alive-lock is
  /// authoritative: held, it is live. Without the lock (a direct instance between connections, or any pooled
  /// instance), the heartbeat decides, against the window for its mode.
  /// </summary>
  /// <param name="candidate">The candidate.</param>
  /// <param name="options">The windows.</param>
  /// <returns>True when live.</returns>
  public static bool IsLive(PartitionAssignmentCandidate candidate, PartitionAssignerOptions options) {
    ArgumentNullException.ThrowIfNull(candidate);
    ArgumentNullException.ThrowIfNull(options);
    if (candidate.Mode == InstanceConnectionMode.Direct) {
      return candidate.AliveLockHeld || candidate.HeartbeatAge <= options.DirectHeartbeatWindow;
    }
    return candidate.HeartbeatAge <= options.PooledHeartbeatWindow;
  }

  /// <summary>
  /// The live members, in instance-id order. The assigner itself is always a member: it is running the evaluation.
  /// </summary>
  /// <param name="candidates">The registered instances and their signals.</param>
  /// <param name="self">The assigner.</param>
  /// <param name="options">The windows.</param>
  /// <returns>The members, in rank order.</returns>
  public static IReadOnlyList<Guid> LiveMembers(
      IEnumerable<PartitionAssignmentCandidate> candidates, Guid self, PartitionAssignerOptions options) {
    ArgumentNullException.ThrowIfNull(candidates);
    var live = new SortedSet<Guid>(candidates.Where(c => IsLive(c, options)).Select(c => c.InstanceId)) { self };
    return [.. live];
  }

  /// <summary>
  /// Whether the live members call for a publish: no assignment of this tenure yet, a change in membership, or the
  /// backstop come due.
  /// </summary>
  /// <param name="members">The live members, in rank order.</param>
  /// <param name="published">What this tenure last published, or null.</param>
  /// <param name="epoch">This tenure's role epoch.</param>
  /// <param name="sincePublished">How long ago, by the assigner's own clock, it last published.</param>
  /// <param name="options">The backstop cadence.</param>
  /// <returns>The evaluation.</returns>
  public static PartitionAssignerEvaluation Evaluate(
      IReadOnlyList<Guid> members, PartitionAssignment? published, long epoch, TimeSpan sincePublished,
      PartitionAssignerOptions options) {
    ArgumentNullException.ThrowIfNull(members);
    ArgumentNullException.ThrowIfNull(options);
    return new PartitionAssignerEvaluation(members, _reason(members, published, epoch, sincePublished, options));
  }

  private static PartitionAssignerReason _reason(
      IReadOnlyList<Guid> members, PartitionAssignment? published, long epoch, TimeSpan sincePublished,
      PartitionAssignerOptions options) {
    if (published is null || published.Epoch != epoch) {
      return PartitionAssignerReason.NewTenure;
    }
    if (!published.Members.SequenceEqual(members)) {
      return PartitionAssignerReason.MembershipChanged;
    }
    return sincePublished >= options.RepublishInterval ? PartitionAssignerReason.Backstop : PartitionAssignerReason.Unchanged;
  }
}
