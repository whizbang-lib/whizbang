// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// One tick of the assigner's tenure (#1254), driven directly: no hosted loop, no threads, a fake clock and an
/// in-memory store. The loop itself runs against Postgres in PartitionAssignerWorkerPostgresTests.
/// </summary>
[Category("Workers")]
public class PartitionAssignerWorkerTests {
  private static readonly Guid _self = new("00000000-0000-0000-0000-000000000005");
  private static readonly Guid _peer = new("00000000-0000-0000-0000-00000000000a");

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId => _self;
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = _self, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class NoElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
  }

  private static (PartitionAssignerWorker Worker, FakePartitionAssignmentStore Store, FakeAliveLock Lock, FakeTimeProvider Clock) _create(
      PartitionAssignerOptions? options = null) {
    var store = new FakePartitionAssignmentStore();
    var aliveLock = new FakeAliveLock();
    var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
    var worker = new PartitionAssignerWorker(
      new NoElector(), store, aliveLock, new Instance(),
      Options.Create(options ?? new PartitionAssignerOptions()),
      Options.Create(new RoleAssignmentOptions()),
      NullLogger<PartitionAssignerWorker>.Instance, clock);
    // The assigner's own registration: eligible for work, so a candidate (#1638). Kept last so a test's own
    // candidates are indexed from zero.
    store.Candidates.Add(_pooled(_self, 1));
    return (worker, store, aliveLock, clock);
  }

  private static PartitionAssignmentCandidate _pooled(Guid id, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Pooled, false, TimeSpan.FromSeconds(heartbeatAgeSeconds), IsLive: heartbeatAgeSeconds < 60);

  [Test]
  public async Task Tick_TheFirstOfATenure_PublishesTheLiveMembersAsync() {
    var (worker, store, _, _) = _create();
    store.Candidates.Insert(0, _pooled(_peer, 1));
    var became = new List<long>();
    var published = new List<PartitionAssignment>();
    worker.OnBecameAssigner += became.Add;
    worker.OnPublished += published.Add;

    var continues = await worker.TickAsync(new FakeDutyGrant(7), CancellationToken.None);

    await Assert.That(continues).IsTrue();
    await Assert.That(became).IsEquivalentTo([7L]);
    await Assert.That(worker.IsAssigner).IsTrue();
    await Assert.That(worker.Epoch).IsEqualTo(7);
    await Assert.That(published).Count().IsEqualTo(1);
    await Assert.That(published[0].Members).IsEquivalentTo([_self, _peer], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(worker.LastPublished).IsEqualTo(published[0]);
  }

  [Test]
  public async Task Tick_WithNothingChanged_DoesNotPublish_AndRenewsWhileTheLockIsHeldAsync() {
    var (worker, store, aliveLock, _) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    aliveLock.IsAliveLockHeld = true;
    var evaluations = new List<PartitionAssignerEvaluation>();
    worker.OnEvaluated += evaluations.Add;

    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Publishes).Count().IsEqualTo(1);
    await Assert.That(evaluations.Single().Reason).IsEqualTo(PartitionAssignerReason.Unchanged);
    await Assert.That(store.Renewals).IsEquivalentTo([(_self, 7L)])
      .Because("the alive-lock is the assigner's liveness, so its tick renews the lease");
  }

  [Test]
  public async Task Tick_WithNothingChanged_AndNoLock_LeavesTheRenewalToTheHeartbeatAsync() {
    var (worker, store, _, _) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Renewals).IsEmpty();
  }

  [Test]
  public async Task Tick_WhenAPeerGoesStale_RepublishesWithoutIt_OnceTheChangeHasSettledAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    store.Candidates.Insert(0, _pooled(_peer, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    store.Candidates[0] = _pooled(_peer, 3600);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(new PartitionAssignerOptions().SettleWindow);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Publishes).Count().IsEqualTo(2);
    await Assert.That(worker.LastPublished!.Members).IsEquivalentTo([_self]);
    await Assert.That(worker.LastPublished!.Revision).IsEqualTo(2);
  }

  [Test]
  public async Task Tick_AtTheBackstop_RepublishesUnchangedAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    clock.Advance(new PartitionAssignerOptions().RepublishInterval);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Publishes).Count().IsEqualTo(2);
    await Assert.That(store.Publishes[1].MovesTo(store.Publishes[0])).IsEqualTo(0)
      .Because("the backstop republishes the map it has, unchanged");
  }

  // --- the map and the settle window (#2558) ----------------------------------------------------

  private static readonly Guid _joinerA = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _joinerB = new("00000000-0000-0000-0000-00000000000c");
  private static readonly TimeSpan _window = new PartitionAssignerOptions().SettleWindow;

  [Test]
  public async Task Tick_KJoinsInsideTheSettleWindow_PublishOnceAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    var reasons = new List<PartitionAssignerReason>();
    worker.OnEvaluated += e => reasons.Add(e.Reason);

    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(TimeSpan.FromSeconds(2));
    store.Candidates.Add(_pooled(_joinerB, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(_window - TimeSpan.FromSeconds(2));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Publishes).Count().IsEqualTo(2)
      .Because("two joins inside one window publish one map, not one per join");
    await Assert.That(reasons).IsEquivalentTo(
      [PartitionAssignerReason.Settling, PartitionAssignerReason.Settling, PartitionAssignerReason.MembershipChanged],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the window runs from the first change seen, so a steady trickle of joins cannot postpone the publish");
    await Assert.That(worker.LastPublished!.Members).IsEquivalentTo([_self, _joinerA, _joinerB]);
  }

  [Test]
  public async Task Tick_AfterAPublish_TheNextChangeWaitsAWindowOfItsOwnAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(_window);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    store.Candidates.Add(_pooled(_joinerB, 1));
    clock.Advance(_window);
    var evaluations = new List<PartitionAssignerEvaluation>();
    worker.OnEvaluated += evaluations.Add;
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(evaluations.Single().Reason).IsEqualTo(PartitionAssignerReason.Settling);
    await Assert.That(store.Publishes).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Tick_AChangeThatRevertsInsideTheWindow_PublishesNothing_AndTheNextChangeStartsAFreshWindowAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    var reasons = new List<PartitionAssignerReason>();
    worker.OnEvaluated += e => reasons.Add(e.Reason);

    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(TimeSpan.FromSeconds(3));
    store.Candidates.RemoveAt(store.Candidates.Count - 1);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(TimeSpan.FromSeconds(3));
    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Publishes).Count().IsEqualTo(1).Because("a flap that settles back moves nothing");
    await Assert.That(reasons).IsEquivalentTo(
      [PartitionAssignerReason.Settling, PartitionAssignerReason.Unchanged, PartitionAssignerReason.Settling],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Tick_WhileSettling_RenewsTheLeaseWhileTheLockIsHeldAsync() {
    var (worker, store, aliveLock, _) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    aliveLock.IsAliveLockHeld = true;

    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(store.Renewals).IsEquivalentTo([(_self, 7L)])
      .Because("the published map stays valid while the change settles, so its lease must not lapse");
  }

  [Test]
  public async Task Tick_AJoin_PublishesAMapThatMovesOnlyTheJoinersShareAsync() {
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    store.Candidates.Insert(0, _pooled(_peer, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    store.Candidates.Add(_pooled(_joinerB, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(_window);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    var before = store.Publishes[0];
    var after = store.Publishes[1];
    await Assert.That(after.MovesTo(before)).IsEqualTo(after.CountOf(_joinerB));
    await Assert.That(after.CountOf(_joinerB)).IsEqualTo(PartitionMap.PARTITION_COUNT / 3);
    await Assert.That(Enumerable.Range(0, PartitionMap.PARTITION_COUNT)
      .Where(p => after.OwnerOf(p) != before.OwnerOf(p)).All(p => after.OwnerOf(p) == _joinerB)).IsTrue();
    await Assert.That(worker.LastPublished!.Owners).IsEquivalentTo(after.Owners, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the published assignment carries the map, not just the member list");
  }

  [Test]
  public async Task Tick_ANewTenure_StartsFromThePublishedMap_SoItMovesNothingAsync() {
    var (worker, store, _, _) = _create();
    store.Candidates.Insert(0, _pooled(_peer, 1));
    var previous = PartitionMap.Initial([_self, _peer, _joinerA]).Rebalance([_self, _peer]);
    store.Published = new PartitionAssignmentRead(
      new PartitionAssignment(6, 3, _peer, previous.Members, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), previous.Owners),
      TimeSpan.FromSeconds(30));

    _ = await worker.TickAsync(new FakeDutyGrant(7), CancellationToken.None);

    await Assert.That(PartitionMap.Initial([_self, _peer]).MovesTo(previous)).IsGreaterThan(0)
      .Because("the control: the previous tenure's map is not the modulo, so starting afresh would move partitions");
    await Assert.That(store.Publishes.Single().MovesTo(previous)).IsEqualTo(0)
      .Because("a new assigner continues from the published map; a failover alone moves nothing");
  }

  [Test]
  public async Task Tick_AnInstanceNotYetReady_IsGivenNothing_UntilItIsACandidateAsync() {
    // The candidates are the instances whose startup reports ready (wh_partition_assignment_candidates, #1638): one
    // still starting is not among them, so it is not a member and the map gives it nothing.
    var (worker, store, _, clock) = _create();
    var grant = new FakeDutyGrant(7);
    _ = await worker.TickAsync(grant, CancellationToken.None);
    var whileStarting = store.Publishes.Single();

    store.Candidates.Add(_pooled(_joinerA, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);
    clock.Advance(_window);
    _ = await worker.TickAsync(grant, CancellationToken.None);

    await Assert.That(whileStarting.CountOf(_joinerA)).IsEqualTo(0);
    await Assert.That(store.Publishes[1].CountOf(_joinerA)).IsEqualTo(PartitionMap.PARTITION_COUNT / 2);
  }

  [Test]
  public async Task Tick_RefusedByTheFence_EndsTheTenureAsync() {
    var (worker, store, _, _) = _create();
    store.RefusePublish = true;
    var evaluations = 0;
    worker.OnEvaluated += _ => evaluations++;

    var continues = await worker.TickAsync(new FakeDutyGrant(7), CancellationToken.None);

    await Assert.That(continues).IsFalse();
    await Assert.That(worker.LastPublished).IsNull();
    await Assert.That(evaluations).IsEqualTo(1);
  }

  [Test]
  public async Task Tick_UnderANewTenure_BeginsItAndPublishesAgainAsync() {
    var (worker, store, _, _) = _create();
    _ = await worker.TickAsync(new FakeDutyGrant(7), CancellationToken.None);

    _ = await worker.TickAsync(new FakeDutyGrant(8), CancellationToken.None);

    await Assert.That(worker.Epoch).IsEqualTo(8);
    await Assert.That(store.Publishes).Count().IsEqualTo(2);
    await Assert.That(worker.LastPublished!.Epoch).IsEqualTo(8);
  }

  [Test]
  public async Task Tick_WithAGrantWithoutAnEpoch_IsRefusedAsync() {
    var (worker, _, _, _) = _create();

    await Assert.That(async () => await worker.TickAsync(new FakeDutyGrant(null), CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task NotAssigner_ReportsNoEpochAsync() {
    var (worker, _, _, _) = _create();

    await Assert.That(worker.IsAssigner).IsFalse();
    await Assert.That(worker.Epoch).IsNull();
    await Assert.That(worker.LastPublished).IsNull();
  }

  [Test]
  public async Task Constructor_RequiresItsDependenciesAsync() {
    var store = new FakePartitionAssignmentStore();
    var aliveLock = new FakeAliveLock();
    var options = Options.Create(new PartitionAssignerOptions());
    var roles = Options.Create(new RoleAssignmentOptions());
    var logger = NullLogger<PartitionAssignerWorker>.Instance;
    var elector = new NoElector();
    var instance = new Instance();

    await Assert.That(() => new PartitionAssignerWorker(null!, store, aliveLock, instance, options, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, null!, aliveLock, instance, options, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, store, null!, instance, options, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, store, aliveLock, null!, options, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, store, aliveLock, instance, null!, roles, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, store, aliveLock, instance, options, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionAssignerWorker(elector, store, aliveLock, instance, options, roles, null!)).Throws<ArgumentNullException>();
  }
}
