// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>Options for the elected partition assigner (#1254).</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <remarks>
/// The assigner always runs, in every driver (#2559): the published assignment is the one source of stream ownership,
/// so there is no switch that turns it off. A configured <c>Whizbang:Database:PartitionAssigner:Enabled</c> is reported
/// at startup as a removed key and changes nothing.
/// </remarks>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:Validate_ALeaseNoLongerThanTheSlowHeartbeat_IsRefusedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs:Removed_WhizbangCoreDeclaresEveryFormerTableEntry_ThroughTheAttributeAsync</tests>
[Whizbang.Core.Configuration.RemovedConfigurationKey("Whizbang:Database:PartitionAssigner:Enabled")]
public sealed class PartitionAssignerOptions {
  /// <summary>The role the assigner is elected to through the role election.</summary>
  public const string ROLE = "partition-assigner";

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

  /// <summary>
  /// Whether the assigner turns ownership by map on for this service (#2555): once every live instance of the service
  /// supports it, the assigner writes the service's one-way switch, and from then on every claim decides through the
  /// partition ownership table alone (<c>wh_i_own</c>), with no stored owner and no ranking of its own. Default false:
  /// the handover and fencing that make a moved partition safe arrive in later phases of #2540, and the default turns on
  /// with them. The ownership table is written with every publish either way, so it is ready when the switch flips.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerWorkerTests.cs:Tick_WithOwnershipByMapOff_NeverAsksForTheSwitchAsync</tests>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerWorkerTests.cs:Tick_WithOwnershipByMapOn_AsksForTheSwitchAfterPublishing_AtItsEpochAsync</tests>
  public bool OwnershipByMap { get; set; }

  /// <summary>
  /// How long a membership change is collected before one new map is published (#2558): the window runs from the first
  /// change the assigner sees, every join and leave inside it lands in the same publish, and a change that reverts
  /// inside it publishes nothing. A rolling deploy of K instances then causes K small moves, not a reshuffle per
  /// heartbeat. A new tenure publishes at once, and the published map stays valid (and is renewed) while a change
  /// settles. Default 5 seconds; zero publishes every change at once. Bound from
  /// <c>Whizbang:Database:PartitionAssigner:SettleWindow</c>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#settle-window</docs>
  /// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerWorkerTests.cs:Tick_KJoinsInsideTheSettleWindow_PublishOnceAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerRegistrationTests.cs:AddWhizbangPostgresNotifications_BindsEveryAssignerKeyFromConfigurationAsync</tests>
  public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>Throws when the options cannot work together.</summary>
  /// <exception cref="InvalidOperationException">The backstop is not positive, the lease is too short, or the settle
  /// window is negative.</exception>
  public void Validate() {
    if (SettleWindow < TimeSpan.Zero) {
      throw new InvalidOperationException("PartitionAssigner.SettleWindow must not be negative.");
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

  /// <summary>
  /// An instance became live or stopped being live (an alive-lock gained or lost, a heartbeat fresh or stale), and the
  /// settle window since the first such change has run out.
  /// </summary>
  MembershipChanged = 2,

  /// <summary>Nothing changed, and the backstop cadence came due.</summary>
  Backstop = 3,

  /// <summary>
  /// The membership changed, and the settle window since the first change seen has not run out: no publish yet (#2558).
  /// </summary>
  Settling = 4,
}

/// <summary>One evaluation by the assigner: the live members it found and whether that calls for a publish.</summary>
/// <param name="Members">The live instances, in rank order.</param>
/// <param name="Reason">Why it publishes, or <see cref="PartitionAssignerReason.Unchanged"/>.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public sealed record PartitionAssignerEvaluation(IReadOnlyList<Guid> Members, PartitionAssignerReason Reason) {
  /// <summary>Whether this evaluation publishes: not while nothing changed, nor while a change settles (#2558).</summary>
  public bool Publishes => Reason is not (PartitionAssignerReason.Unchanged or PartitionAssignerReason.Settling);
}

/// <summary>
/// The assigner's decisions, with no I/O and no clock of their own: who is live, by the database's one liveness rule
/// (#2027), and whether that calls for a publish.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:IsLive_IsTheRulesAnswer_WhateverTheLockOrTheHeartbeatAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:Evaluate_AMemberGoingStale_PublishesAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerTests.cs:Evaluate_AChangeInsideTheSettleWindow_SettlesWithoutPublishingAsync</tests>
public static class PartitionAssigner {
  /// <summary>
  /// Whether a candidate is live: the answer of the database's one liveness rule (#2027, <c>wh_instance_liveness</c>),
  /// which the candidate carries. The assigner keeps no window of its own, so it can never call an instance live that the
  /// claim and the reap call dead.
  /// </summary>
  /// <param name="candidate">The candidate.</param>
  /// <returns>True when live.</returns>
  public static bool IsLive(PartitionAssignmentCandidate candidate) {
    ArgumentNullException.ThrowIfNull(candidate);
    return candidate.IsLive;
  }

  /// <summary>
  /// The live members, in instance-id order. The candidates are the instances eligible for work (#1638): a starting or
  /// draining instance is not among them. The assigner is a member when it is a candidate, whatever its heartbeat's age,
  /// since it is running the evaluation; when it is not a candidate (still starting, or draining) it gives itself no
  /// share, as it gives none to any other instance that cannot claim one.
  /// </summary>
  /// <param name="candidates">The registered instances and their signals.</param>
  /// <param name="self">The assigner.</param>
  /// <returns>The members, in rank order.</returns>
  public static IReadOnlyList<Guid> LiveMembers(IEnumerable<PartitionAssignmentCandidate> candidates, Guid self) {
    ArgumentNullException.ThrowIfNull(candidates);
    var live = new SortedSet<Guid>(candidates.Where(c => c.InstanceId == self || IsLive(c)).Select(c => c.InstanceId));
    return [.. live];
  }

  /// <summary>
  /// Whether the live members call for a publish: no assignment of this tenure yet, a change in membership that has
  /// settled (#2558), or the backstop come due.
  /// </summary>
  /// <param name="members">The live members, in rank order.</param>
  /// <param name="published">What this tenure last published, or null.</param>
  /// <param name="epoch">This tenure's role epoch.</param>
  /// <param name="sincePublished">How long ago, by the assigner's own clock, it last published.</param>
  /// <param name="unsettledFor">How long, by the assigner's own clock, the members have differed from the published ones
  /// (#2558): since the first change seen, zero when they have just started to differ.</param>
  /// <param name="options">The backstop cadence.</param>
  /// <returns>The evaluation.</returns>
  public static PartitionAssignerEvaluation Evaluate(
      IReadOnlyList<Guid> members, PartitionAssignment? published, long epoch, TimeSpan sincePublished,
      TimeSpan unsettledFor, PartitionAssignerOptions options) {
    ArgumentNullException.ThrowIfNull(members);
    ArgumentNullException.ThrowIfNull(options);
    return new PartitionAssignerEvaluation(members, _reason(members, published, epoch, sincePublished, unsettledFor, options));
  }

  private static PartitionAssignerReason _reason(
      IReadOnlyList<Guid> members, PartitionAssignment? published, long epoch, TimeSpan sincePublished,
      TimeSpan unsettledFor, PartitionAssignerOptions options) {
    if (published is null || published.Epoch != epoch) {
      return PartitionAssignerReason.NewTenure;
    }
    if (!published.Members.SequenceEqual(members)) {
      // #2558: the change waits out the settle window, so the joins and leaves inside it land in one publish.
      return unsettledFor >= options.SettleWindow ? PartitionAssignerReason.MembershipChanged : PartitionAssignerReason.Settling;
    }
    return sincePublished >= options.RepublishInterval ? PartitionAssignerReason.Backstop : PartitionAssignerReason.Unchanged;
  }
}
