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

  private static PartitionAssignmentCandidate _direct(Guid id, bool lockHeld, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Direct, lockHeld, TimeSpan.FromSeconds(heartbeatAgeSeconds));

  private static PartitionAssignmentCandidate _pooled(Guid id, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Pooled, AliveLockHeld: false, TimeSpan.FromSeconds(heartbeatAgeSeconds));

  private static PartitionAssignment _published(long epoch, params Guid[] members) =>
    new(epoch, 1, _self, members, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2));

  // --- liveness per connection mode ------------------------------------------------------------

  [Test]
  public async Task IsLive_ADirectInstanceHoldingItsLock_IsLiveWhateverItsHeartbeatAsync() {
    await Assert.That(PartitionAssigner.IsLive(_direct(_a, lockHeld: true, heartbeatAgeSeconds: 3600), _options)).IsTrue();
  }

  [Test]
  public async Task IsLive_ADirectInstanceWithoutItsLock_FallsBackToItsHeartbeatAsync() {
    await Assert.That(PartitionAssigner.IsLive(_direct(_a, lockHeld: false, heartbeatAgeSeconds: 179), _options)).IsTrue()
      .Because("a lost lock is judged by the heartbeat, over the window that covers the slow beats a lock holder makes");
    await Assert.That(PartitionAssigner.IsLive(_direct(_a, lockHeld: false, heartbeatAgeSeconds: 181), _options)).IsFalse();
  }

  [Test]
  public async Task IsLive_APooledInstance_IsJudgedByItsHeartbeatAloneAsync() {
    await Assert.That(PartitionAssigner.IsLive(_pooled(_a, 89), _options)).IsTrue();
    await Assert.That(PartitionAssigner.IsLive(_pooled(_a, 91), _options)).IsFalse();
  }

  [Test]
  public async Task IsLive_APooledInstanceReportedHoldingALock_IsStillJudgedByItsHeartbeatAsync() {
    var candidate = new PartitionAssignmentCandidate(_a, InstanceConnectionMode.Pooled, AliveLockHeld: true, TimeSpan.FromMinutes(10));

    await Assert.That(PartitionAssigner.IsLive(candidate, _options)).IsFalse()
      .Because("the lock is the authority only for an instance that registered a direct connection");
  }

  [Test]
  public async Task LiveMembers_AreTheLiveInstancesAndTheAssigner_InIdOrderAsync() {
    var members = PartitionAssigner.LiveMembers([_pooled(_b, 1), _pooled(_a, 600), _direct(_a, true, 600)], _self, _options);

    await Assert.That(members).IsEquivalentTo([_self, _a, _b], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the assigner is running the evaluation, and an instance listed twice is one member");
  }

  [Test]
  public async Task LiveMembers_WithNoLivePeer_IsTheAssignerAloneAsync() {
    var members = PartitionAssigner.LiveMembers([_pooled(_a, 600)], _self, _options);

    await Assert.That(members).IsEquivalentTo([_self]);
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
  [Arguments(0, 90)]
  [Arguments(90, 0)]
  public async Task Validate_ANonPositiveWindow_IsRefusedAsync(int pooledSeconds, int directSeconds) {
    var options = new PartitionAssignerOptions {
      PooledHeartbeatWindow = TimeSpan.FromSeconds(pooledSeconds),
      DirectHeartbeatWindow = TimeSpan.FromSeconds(directSeconds),
    };

    await Assert.That(options.Validate).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Validate_ANonPositiveBackstop_IsRefusedAsync() {
    var options = new PartitionAssignerOptions { RepublishInterval = TimeSpan.Zero };

    await Assert.That(options.Validate).Throws<InvalidOperationException>();
  }
}
