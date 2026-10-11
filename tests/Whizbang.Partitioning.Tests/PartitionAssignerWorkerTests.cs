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
    return (worker, store, aliveLock, clock);
  }

  private static PartitionAssignmentCandidate _pooled(Guid id, int heartbeatAgeSeconds) =>
    new(id, InstanceConnectionMode.Pooled, false, TimeSpan.FromSeconds(heartbeatAgeSeconds));

  [Test]
  public async Task Tick_TheFirstOfATenure_PublishesTheLiveMembersAsync() {
    var (worker, store, _, _) = _create();
    store.Candidates.Add(_pooled(_peer, 1));
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
  public async Task Tick_WhenAPeerGoesStale_RepublishesWithoutItAsync() {
    var (worker, store, _, _) = _create();
    var grant = new FakeDutyGrant(7);
    store.Candidates.Add(_pooled(_peer, 1));
    _ = await worker.TickAsync(grant, CancellationToken.None);

    store.Candidates[0] = _pooled(_peer, 3600);
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
