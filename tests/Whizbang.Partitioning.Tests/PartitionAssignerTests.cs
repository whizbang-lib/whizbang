// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// The assigner's decisions (#1254): liveness by the strategy for each connection mode, and when the members call for
/// a publish. Pure: no clock, no I/O.
/// </summary>
[Category("Workers")]
public class PartitionAssignerTests {
  private static readonly Guid _self = new("00000000-0000-0000-0000-000000000005");
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly PartitionAssignerOptions _options = new();

  // #2027: the database's one liveness rule answers IsLive; these fixtures state its answer for the case they describe.
  private static PartitionAssignmentCandidate _direct(Guid id, bool lockHeld, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Direct, lockHeld, TimeSpan.FromSeconds(heartbeatAgeSeconds), IsLive: lockHeld || heartbeatAgeSeconds < 60);

  private static PartitionAssignmentCandidate _pooled(Guid id, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Pooled, AliveLockHeld: false, TimeSpan.FromSeconds(heartbeatAgeSeconds), IsLive: heartbeatAgeSeconds < 60);

  private static PartitionAssignment _published(long epoch, params Guid[] members) =>
    new(epoch, 1, _self, members, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2));

  // --- liveness: the rule's answer -------------------------------------------------------------

  [Test]
  public async Task IsLive_IsTheRulesAnswer_WhateverTheLockOrTheHeartbeatAsync() {
    // #2027: one liveness rule, in SQL. A copy here, with windows of its own, could call an instance live that the
    // claim and the reap call dead, and the assigner would hand out a share nobody claims.
    var deadThoughLockedAndFresh = new PartitionAssignmentCandidate(_a, InstanceConnectionMode.Direct, AliveLockHeld: true, TimeSpan.Zero, IsLive: false);
    var liveThoughOldAndUnlocked = new PartitionAssignmentCandidate(_b, InstanceConnectionMode.Pooled, AliveLockHeld: false, TimeSpan.FromHours(1), IsLive: true);

    await Assert.That(PartitionAssigner.IsLive(deadThoughLockedAndFresh)).IsFalse();
    await Assert.That(PartitionAssigner.IsLive(liveThoughOldAndUnlocked)).IsTrue();
  }

  [Test]
  public async Task LiveMembers_AreTheLiveInstancesAndTheAssigner_InIdOrderAsync() {
    var members = PartitionAssigner.LiveMembers(
      [_pooled(_b, 1), _pooled(_a, 600), _direct(_a, true, 600), _pooled(_self, 600)], _self);

    await Assert.That(members).IsEquivalentTo([_self, _a, _b], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the assigner is running the evaluation, and an instance listed twice is one member");
  }

  [Test]
  public async Task LiveMembers_WithNoLivePeer_IsTheAssignerAloneAsync() {
    var members = PartitionAssigner.LiveMembers([_pooled(_a, 600), _pooled(_self, 1)], _self);

    await Assert.That(members).IsEquivalentTo([_self]);
  }

  [Test]
  public async Task LiveMembers_TheAssignerAmongTheCandidates_IsAMemberWhateverItsHeartbeatAsync() {
    var members = PartitionAssigner.LiveMembers([_pooled(_self, 3600), _pooled(_a, 1)], _self);

    await Assert.That(members).IsEquivalentTo([_self, _a], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the assigner is running the evaluation, so a late beat of its own does not count it out");
  }

  [Test]
  public async Task LiveMembers_TheAssignerNotAmongTheCandidates_IsNotAMemberAsync() {
    // #1638: the candidates are the instances eligible for work. An assigner elected while it is still migrating (or
    // once it is draining) is alive but not eligible, so it is not a candidate and must not give itself a share.
    var members = PartitionAssigner.LiveMembers([_pooled(_a, 1)], _self);

    await Assert.That(members).IsEquivalentTo([_a])
      .Because("a share assigned to an instance that cannot claim it stalls until the instance is ready");
  }

  [Test]
  public async Task LiveMembers_WithNoCandidateAtAll_IsEmptyAsync() {
    var members = PartitionAssigner.LiveMembers([], _self);

    await Assert.That(members).IsEmpty()
      .Because("with nobody eligible every claimer ranks itself, which an empty assignment leaves it to do");
  }

  // --- when to publish -------------------------------------------------------------------------

  [Test]
  public async Task Evaluate_NothingPublishedYet_PublishesAsANewTenureAsync() {
    var evaluation = PartitionAssigner.Evaluate([_self, _a], published: null, epoch: 3, TimeSpan.Zero, _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.NewTenure);
    await Assert.That(evaluation.Publishes).IsTrue();
  }

  [Test]
  public async Task Evaluate_AnotherTenuresAssignment_PublishesAsANewTenureAsync() {
    var evaluation = PartitionAssigner.Evaluate([_self, _a], _published(2, _self, _a), epoch: 3, TimeSpan.Zero, _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.NewTenure);
  }

  [Test]
  public async Task Evaluate_AMemberGoingStale_PublishesAsync() {
    var evaluation = PartitionAssigner.Evaluate([_self], _published(3, _self, _a), epoch: 3, TimeSpan.FromSeconds(5), _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.MembershipChanged);
  }

  [Test]
  public async Task Evaluate_AnInstanceBecomingLive_PublishesAsync() {
    var evaluation = PartitionAssigner.Evaluate([_self, _a, _b], _published(3, _self, _a), epoch: 3, TimeSpan.FromSeconds(5), _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.MembershipChanged);
  }

  [Test]
  public async Task Evaluate_NoChange_BeforeTheBackstop_DoesNotPublishAsync() {
    var evaluation = PartitionAssigner.Evaluate(
      [_self, _a], _published(3, _self, _a), epoch: 3, _options.RepublishInterval - TimeSpan.FromTicks(1), _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.Unchanged);
    await Assert.That(evaluation.Publishes).IsFalse();
  }

  [Test]
  public async Task Evaluate_NoChange_AtTheBackstop_PublishesAsync() {
    var evaluation = PartitionAssigner.Evaluate([_self, _a], _published(3, _self, _a), epoch: 3, _options.RepublishInterval, _options);

    await Assert.That(evaluation.Reason).IsEqualTo(PartitionAssignerReason.Backstop);
  }

  // --- options ---------------------------------------------------------------------------------

  [Test]
  public async Task Validate_TheDefaults_PassAsync() {
    await Assert.That(() => new PartitionAssignerOptions().Validate()).ThrowsNothing();
  }

  [Test]
  public async Task Validate_ALeaseNoLongerThanTheSlowHeartbeat_IsRefusedAsync() {
    var options = new PartitionAssignerOptions { AssignmentLease = TimeSpan.FromSeconds(60) };

    await Assert.That(options.Validate).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Validate_ANonPositiveBackstop_IsRefusedAsync() {
    var options = new PartitionAssignerOptions { RepublishInterval = TimeSpan.Zero };

    await Assert.That(options.Validate).Throws<InvalidOperationException>();
  }
}
